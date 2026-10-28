using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Xunit;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using WireGamePhase = Mahjong.Contracts.Wire.GamePhase;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalHostRoundTests
    {
        [Fact]
        public async Task FullRoundCanBePlayedOfflineThroughClientCommands()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            RoundPlay play = await PlayRoundAsync(harness, ct);
            try
            {
                Assert.True(play.Actions > 0, "no client action was accepted");
                Assert.True(
                    play.Final.PublicView.Board.Phase == WireGamePhase.RoundEnd ||
                    play.Final.PublicView.Session.Finished,
                    "round did not end; last phase=" + play.Final.PublicView.Board.Phase +
                    " version=" + play.Final.StateVersion +
                    " seat=" + play.Final.PublicView.Board.CurrentSeat);
                Assert.True(play.Final.StateVersion > 0);
            }
            finally
            {
                await play.DisposeAsync();
            }
        }

        [Fact]
        public async Task TerminalRoundSurvivesColdRestart()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            RoundPlay play = await PlayRoundAsync(harness, ct);
            PlayerSnapshot final;
            try
            {
                final = play.Final;
            }
            finally
            {
                await play.DisposeAsync();
            }

            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "p41-test", null), ct);
            var restored = new TableSession(channel, new InMemoryOutboxStore());
            string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
            try
            {
                await restored.OpenAsync(new TableSessionOpenRequest(sessionTicket, "p41-test"), ct);
                for (int i = 0; i < 20 && restored.State != TableConnectionState.Ready; i++)
                {
                    await restored.PumpOnceAsync(ct);
                }

                Assert.Equal(TableConnectionState.Ready, restored.State);
                Assert.Equal(final.StateVersion, restored.PlayerSnapshot!.StateVersion);
                Assert.Equal(final.RoundId, restored.PlayerSnapshot.RoundId);
                Assert.Equal(WireGamePhase.RoundEnd, restored.PlayerSnapshot.PublicView.Board.Phase);
                Assert.Equal(
                    final.PublicView.Session.FinalScores.Select(v => v.Score).ToArray(),
                    restored.PlayerSnapshot.PublicView.Session.FinalScores.Select(v => v.Score).ToArray());
            }
            finally
            {
                await restored.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        internal static async Task<RoundPlay> PlayRoundAsync(P41Harness harness, CancellationToken ct)
        {
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "p41-test", null), ct);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pumpCts.CancelAfter(TimeSpan.FromSeconds(40));
            Task? pump = null;
            try
            {
                await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "p41-test"), pumpCts.Token);
                pump = Task.Run(async () =>
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
                await WaitUntilAsync(() => session.State == TableConnectionState.Ready, pumpCts.Token);

                int actions = 0;
                for (int step = 0; step < 400; step++)
                {
                    PlayerSnapshot? snapshot = session.PlayerSnapshot;
                    if (snapshot == null)
                    {
                        await Task.Delay(10, pumpCts.Token);
                        continue;
                    }

                    if (snapshot.PublicView.Session.Finished ||
                        snapshot.PublicView.Board.Phase == WireGamePhase.RoundEnd)
                    {
                        break;
                    }

                    bool isMyTurn = snapshot.PublicView.Board.CurrentSeat == 0;
                    bool isResponse = snapshot.PublicView.Board.Phase == WireGamePhase.ResponseWindow ||
                                      snapshot.PublicView.Board.Phase == WireGamePhase.ChankanWindow;
                    if ((isMyTurn || isResponse) &&
                        snapshot.PrivateView != null &&
                        snapshot.PrivateView.Seat == 0)
                    {
                        bool sent = false;
                        switch (snapshot.PublicView.Board.Phase)
                        {
                            case WireGamePhase.Draw:
                            case WireGamePhase.KanDraw:
                                sent = await TryHumanActionAsync(
                                    session,
                                    snapshot,
                                    ProtocolActionType.DrawTile,
                                    null,
                                    pumpCts.Token);
                                break;
                            case WireGamePhase.CheatWindow:
                            case WireGamePhase.Discard:
                                uint tile = snapshot.PrivateView.HasDrawnTileId
                                    ? snapshot.PrivateView.DrawnTileId
                                    : snapshot.PrivateView.ClosedTiles[0];
                                sent = await TryHumanActionAsync(
                                    session,
                                    snapshot,
                                    ProtocolActionType.DiscardTile,
                                    new[] { tile },
                                    pumpCts.Token);
                                break;
                            case WireGamePhase.ResponseWindow:
                            case WireGamePhase.ChankanWindow:
                                sent = await TryHumanActionAsync(
                                    session,
                                    snapshot,
                                    ProtocolActionType.Pass,
                                    null,
                                    pumpCts.Token);
                                break;
                        }

                        if (sent)
                        {
                            actions++;
                        }
                    }

                    await Task.Delay(20, pumpCts.Token);
                }

                PlayerSnapshot final = session.PlayerSnapshot!;
                pumpCts.Cancel();
                if (pump != null)
                {
                    try
                    {
                        await pump;
                    }
                    catch
                    {
                    }
                }

                return new RoundPlay(channel, session, final, actions);
            }
            catch
            {
                pumpCts.Cancel();
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
                throw;
            }
        }

        private static async Task<bool> TryHumanActionAsync(
            TableSession session,
            PlayerSnapshot snapshot,
            ProtocolActionType action,
            uint[]? tiles,
            CancellationToken cancellationToken)
        {
            var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<TableSnapshotChangedEventArgs> handler = (_, _) =>
            {
                if (session.PlayerSnapshot != null && session.PlayerSnapshot.StateVersion > snapshot.StateVersion)
                {
                    changed.TrySetResult(true);
                }
            };
            session.SnapshotChanged += handler;
            try
            {
                Assert.True(
                    session.State == TableConnectionState.Ready,
                    "state=" + session.State + " last=" + session.LastError);
                string roundId = snapshot.RoundId;
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    roundId,
                    "local-direct",
                    0UL,
                    action,
                    tileIds: tiles);
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    new RequestId("human-" + Guid.NewGuid().ToString("N")),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snapshot.StateVersion),
                    new RoundId(roundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(0));
                CommandSubmission submission = await session.SubmitAsync(command, cancellationToken);
                if (!submission.Sent)
                {
                    return false;
                }

                Task completed = await Task.WhenAny(changed.Task, Task.Delay(200, cancellationToken));
                return completed == changed.Task && await changed.Task;
            }
            finally
            {
                session.SnapshotChanged -= handler;
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 500 && !condition(); i++)
            {
                await Task.Delay(10, cancellationToken);
            }

            Assert.True(condition());
        }

        internal sealed class RoundPlay : IAsyncDisposable
        {
            public RoundPlay(ITableChannel channel, TableSession session, PlayerSnapshot final, int actions)
            {
                Channel = channel;
                Session = session;
                Final = final;
                Actions = actions;
            }

            public ITableChannel Channel { get; }

            public TableSession Session { get; }

            public PlayerSnapshot Final { get; }

            public int Actions { get; }

            public async ValueTask DisposeAsync()
            {
                await Session.CloseAsync("TEST_DONE", CancellationToken.None);
                await Channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }
    }
}
