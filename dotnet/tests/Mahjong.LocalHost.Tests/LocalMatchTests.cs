using System;
using System.Collections.Generic;
using System.IO;
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
    public sealed class LocalMatchTests
    {
        [Fact]
        public async Task TwoRoundsAdvanceTheMatchAndSurviveCoordinatorRestart()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            string root = Path.Combine(harness.Root, "match");
            var options = new LocalMatchHostOptions
            {
                SaveDirectory = root,
                MatchId = "match-1",
                OwnerInstanceId = "owner-match",
                LeaseDuration = TimeSpan.FromMinutes(5),
                Environment = harness.Options.Environment,
                Rules = new Mahjong.Domain.MatchRuleConfig(
                    Mahjong.Domain.MatchLength.Tonpuusen,
                    handsPerWind: 2,
                    allowWestExtension: false),
                DeclareAgariYameAtFinalRound = true
            };

            LocalMatchCoordinator match = await LocalMatchCoordinator.OpenAsync(options, ct);
            int firstOrdinal;
            long firstScoresSum;
            string firstRound;
            try
            {
                LocalMatchRound round1 = await match.OpenRoundAsync(ct);
                firstOrdinal = round1.Ordinal;
                firstRound = round1.RoundId;
                (TableSession session1, PlayerSnapshot final1) = await PlayRoundAsync(round1, ct);
                firstScoresSum = final1.PublicView.Session.FinalScores.Sum(v => v.Score);
                LocalMatchSnapshot after1 = await match.CompleteRoundAsync(ct);
                await session1.CloseAsync("TEST_DONE", CancellationToken.None);
                Assert.Equal(firstOrdinal + 1, after1.Ordinal);
                Assert.Single(after1.Records);
                Assert.Equal(firstRound, after1.Records[0].RoundId);

                LocalMatchRound round2 = await match.OpenRoundAsync(ct);
                Assert.Equal(after1.RoundId, round2.RoundId);
                (TableSession session2, PlayerSnapshot final2) = await PlayRoundAsync(round2, ct);
                LocalMatchSnapshot after2 = await match.CompleteRoundAsync(ct);
                await session2.CloseAsync("TEST_DONE", CancellationToken.None);
                Assert.Equal(2, after2.Records.Count);
                Assert.Equal(firstScoresSum, after2.Records[0].FinalScores.Sum());
                Assert.NotEqual(firstRound, after2.Records[1].RoundId);
                Assert.True(after2.Finished, "two-round quick match did not finish");
                Assert.Equal(4, after2.Rankings.Count);
            }
            finally
            {
                await match.DisposeAsync();
            }

            LocalMatchCoordinator restored = await LocalMatchCoordinator.OpenAsync(options, ct);
            try
            {
                Assert.Equal(2, restored.Snapshot.Records.Count);
                Assert.Equal(firstRound, restored.Snapshot.Records[0].RoundId);
                Assert.True(restored.Snapshot.Finished);
                Assert.Equal(2, restored.Snapshot.Records.Count);
                Assert.Equal(4, restored.Snapshot.Rankings.Count);
            }
            finally
            {
                await restored.DisposeAsync();
            }
        }

        private static async Task<(TableSession Session, PlayerSnapshot Final)> PlayRoundAsync(
            LocalMatchRound round,
            CancellationToken ct)
        {
            var session = new TableSession(round.Channel, new InMemoryOutboxStore());
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pumpCts.CancelAfter(TimeSpan.FromSeconds(30));
            await session.OpenAsync(new TableSessionOpenRequest(round.Ticket, "p41-test"), pumpCts.Token);
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
                for (int i = 0; i < 30 && session.State != TableConnectionState.Ready; i++)
                {
                    await Task.Delay(10, pumpCts.Token);
                }

                Assert.Equal(TableConnectionState.Ready, session.State);
                for (int step = 0; step < 400; step++)
                {
                    PlayerSnapshot? snapshot = session.PlayerSnapshot;
                    if (snapshot == null)
                    {
                        await Task.Delay(10, pumpCts.Token);
                        continue;
                    }

                    if (snapshot.PublicView.Board.Phase == WireGamePhase.RoundEnd ||
                        snapshot.PublicView.Session.Finished)
                    {
                        pumpCts.Cancel();
                        await pump;
                        return (session, snapshot);
                    }

                    if (snapshot.PublicView.Board.CurrentSeat == 0 && snapshot.PrivateView != null)
                    {
                        switch (snapshot.PublicView.Board.Phase)
                        {
                            case WireGamePhase.CheatWindow:
                            case WireGamePhase.Discard:
                                await SendAsync(
                                    session,
                                    snapshot,
                                    ProtocolActionType.DiscardTile,
                                    new[]
                                    {
                                        snapshot.PrivateView.HasDrawnTileId
                                            ? snapshot.PrivateView.DrawnTileId
                                            : snapshot.PrivateView.ClosedTiles[0]
                                    },
                                    pumpCts.Token);
                                break;
                            case WireGamePhase.ResponseWindow:
                            case WireGamePhase.ChankanWindow:
                                await SendAsync(session, snapshot, ProtocolActionType.Pass, null, pumpCts.Token);
                                break;
                            case WireGamePhase.Draw:
                            case WireGamePhase.KanDraw:
                                await SendAsync(session, snapshot, ProtocolActionType.DrawTile, null, pumpCts.Token);
                                break;
                        }
                    }

                    await Task.Delay(20, pumpCts.Token);
                }

                throw new TimeoutException("match round did not finish");
            }
            finally
            {
                pumpCts.Cancel();
            }
        }

        private static async Task SendAsync(
            TableSession session,
            PlayerSnapshot snapshot,
            ProtocolActionType action,
            uint[]? tiles,
            CancellationToken ct)
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
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    snapshot.RoundId,
                    "local-direct",
                    0UL,
                    action,
                    tileIds: tiles);
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    new RequestId("match-" + Guid.NewGuid().ToString("N")),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snapshot.StateVersion),
                    new RoundId(snapshot.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(0));
                CommandSubmission submission = await session.SubmitAsync(command, ct);
                Assert.True(submission.Sent);
                Task completed = await Task.WhenAny(changed.Task, Task.Delay(500, ct));
                Assert.True(completed == changed.Task, "match action did not commit");
            }
            finally
            {
                session.SnapshotChanged -= handler;
            }
        }
    }
}
