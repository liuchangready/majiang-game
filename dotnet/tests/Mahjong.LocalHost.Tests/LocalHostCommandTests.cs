using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    using ContractCursor = Mahjong.Contracts.StreamCursor;
    using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
    using WireDecisionView = Mahjong.Contracts.Wire.DecisionView;

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

        /// <summary>
        /// Waits for an applied PUSH snapshot (not a receipt) that satisfies the
        /// caller's authoritative version/event/decision predicate. The loop is
        /// driven by SnapshotChanged, so a receipt arriving before its PUSH can
        /// never satisfy a condition that still describes the previous state.
        /// </summary>
        internal static async Task<PlayerSnapshot> WaitForPlayerSnapshotAsync(
            TableSession session,
            Func<PlayerSnapshot, bool> predicate,
            string description,
            CancellationToken cancellationToken)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if (predicate == null)
            {
                throw new ArgumentNullException(nameof(predicate));
            }

            var found = new TaskCompletionSource<PlayerSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<TableSnapshotChangedEventArgs> handler = (_, args) =>
            {
                PlayerSnapshot? snapshot = args.PlayerSnapshot;
                if (snapshot == null)
                {
                    return;
                }

                try
                {
                    if (predicate(snapshot))
                    {
                        found.TrySetResult(snapshot);
                    }
                }
                catch (Exception exception)
                {
                    found.TrySetException(exception);
                }
            };

            session.SnapshotChanged += handler;
            try
            {
                PlayerSnapshot? current = session.PlayerSnapshot;
                if (current != null && predicate(current))
                {
                    return current;
                }

                try
                {
                    return await found.Task
                        .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException(
                        description + " timed out after 10s. " + DescribeSession(session),
                        exception);
                }
            }
            finally
            {
                session.SnapshotChanged -= handler;
            }
        }

        internal static string DescribeSession(TableSession session)
        {
            PlayerSnapshot? snapshot = session.PlayerSnapshot;
            WireDecisionView? decision = snapshot?.PrivateView?.Decision;
            string actions = decision == null
                ? "<none>"
                : string.Join(
                    ",",
                    decision.Actions.Select(
                        action => ((ProtocolActionType)action.ActionType).ToString() +
                                  ":" + action.OptionId +
                                  ":" + string.Join("/", action.TileIds)));
            string closed = snapshot?.PrivateView?.ClosedTiles == null
                ? "<none>"
                : string.Join(",", snapshot.PrivateView.ClosedTiles);
            string pending;
            try
            {
                System.Collections.Generic.IReadOnlyList<PendingCommandSnapshot> commands =
                    session.PendingCommands;
                pending = commands.Count == 0
                    ? "<none>"
                    : string.Join(
                        ";",
                        commands.Select(
                            command => command.RequestId +
                                       ":" + command.Kind +
                                       ":" + (command.LastReceipt?.ToString() ?? "<none>") +
                                       ":" + (command.NeedsReconciliation ? "needs-reconcile" : "resolved")));
            }
            catch (Exception exception)
            {
                // PendingCommands is a live view; a pump may resolve an entry while
                // diagnostics enumerate it. Diagnostic output must never replace the
                // real failure with a concurrent-collection exception.
                pending = "<unavailable:" + exception.GetType().Name + ">";
            }

            ContractCursor? cursor = session.ConfirmedCursor;
            return "sessionState=" + session.State +
                   " lastError=" + (session.LastError ?? "<none>") +
                   " cursor=" + (cursor.HasValue
                       ? cursor.Value.StreamVersion.Value + "/" + cursor.Value.ProjectionSeq.Value
                       : "<none>") +
                   " stateVersion=" + (snapshot == null ? "<none>" : snapshot.StateVersion.ToString()) +
                   " eventSeq=" + (snapshot == null ? "<none>" : snapshot.EventSeq.ToString()) +
                   " round=" + (snapshot?.RoundId ?? "<none>") +
                   " decision=" + (decision?.DecisionId ?? "<none>") +
                   " decisionEpoch=" + (decision == null ? "<none>" : decision.ControlEpoch.ToString()) +
                   " actions=[" + actions + "]" +
                   " drawn=" + (snapshot?.PrivateView != null && snapshot.PrivateView.HasDrawnTileId
                       ? snapshot.PrivateView.DrawnTileId.ToString()
                       : "<none>") +
                   " closed=[" + closed + "]" +
                   " pending=[" + pending + "]" +
                   " board=" + DescribeBoard(snapshot);
        }

        private static string DescribeBoard(PlayerSnapshot? snapshot)
        {
            if (snapshot?.PublicView?.Board == null)
            {
                return "<none>";
            }

            var board = snapshot.PublicView.Board;
            var text = new StringBuilder();
            text.Append("phase=").Append(board.Phase)
                .Append(" seat=").Append(board.CurrentSeat);
            for (int seat = 0; seat < board.Players.Count; seat++)
            {
                text.Append(" p").Append(seat).Append(".discards=[")
                    .Append(string.Join(",", board.Players[seat].Discards.Select(discard => discard.TileId)))
                    .Append(']');
            }

            return text.ToString();
        }
    }
}
