using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Rules;
using Xunit;
using DecisionView = Mahjong.Contracts.Wire.DecisionView;
using LegalActionView = Mahjong.Contracts.Wire.LegalActionView;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using WireGamePhase = Mahjong.Contracts.Wire.GamePhase;

namespace Mahjong.LocalHost.Tests
{
    [Collection("LocalHostSerial")]
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

        [Fact]
        public async Task RealClientRiichiWinPaysStickOnceAndNextRoundStartsClean()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            string root = Path.Combine(harness.Root, "match-real-stick");
            var options = new LocalMatchHostOptions
            {
                SaveDirectory = root,
                MatchId = "match-real-stick",
                OwnerInstanceId = "owner-match",
                LeaseDuration = TimeSpan.FromMinutes(5),
                Environment = harness.Options.Environment,
                Rules = new Mahjong.Domain.MatchRuleConfig(
                    Mahjong.Domain.MatchLength.Tonpuusen,
                    handsPerWind: 2,
                    allowWestExtension: false)
            };
            options.RoundFactory = (state, _) => BuildRiichiWinRound(
                state.CurrentRoundId,
                state.Scores,
                state.RiichiSticks);

            string firstRoundId;
            long[] firstFinalScores;
            int han;
            int fu;
            long points;
            LocalMatchCoordinator match = await LocalMatchCoordinator.OpenAsync(options, ct);
            try
            {
                LocalMatchRound round = await match.OpenRoundAsync(ct);
                firstRoundId = round.RoundId;
                await round.Channel.OpenAsync(
                    new TableChannelConnectRequest(round.Ticket, "match-stick", null),
                    ct);
                var session = new TableSession(round.Channel, new InMemoryOutboxStore());
                await session.OpenAsync(
                    new TableSessionOpenRequest(round.Ticket, "match-stick"),
                    ct);
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(30));
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

                PlayerSnapshot snapshot = await WaitForActionAsync(
                    session,
                    ProtocolActionType.DeclareRiichi,
                    pumpCts.Token);
                DecisionView decision = snapshot.PrivateView.Decision!;
                LegalActionView riichi = decision.Actions.First(
                    a => a.ActionType == (uint)ProtocolActionType.DeclareRiichi);
                TableReceiptResolvedEventArgs riichiReceipt = await SubmitActionAsync(
                    session,
                    snapshot,
                    decision,
                    ProtocolActionType.DeclareRiichi,
                    "real-match-riichi",
                    new uint[] { 132u },
                    riichi.OptionId,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, riichiReceipt.Resolution.ReceiptStatus);

                LocalTableRuntime runtime = ((LocalTableChannel)round.Channel).RuntimeForMatch!;
                RoundStateMachine afterRiichi = runtime.ActorForTest.GetRoundStateForHost();
                Assert.Equal(1, afterRiichi.RiichiSticks);
                Assert.Equal(24_000L, afterRiichi.Scores[0]);

                PlayerSnapshot ronSnapshot = await WaitForActionAsync(
                    session,
                    ProtocolActionType.DeclareRon,
                    pumpCts.Token);
                await Task.Delay(100, pumpCts.Token);
                ronSnapshot = session.PlayerSnapshot!;
                DecisionView ronDecision = ronSnapshot.PrivateView.Decision!;
                TableReceiptResolvedEventArgs ronReceipt = await SubmitActionAsync(
                    session,
                    ronSnapshot,
                    ronDecision,
                    ProtocolActionType.DeclareRon,
                    "real-match-ron",
                    null,
                    null,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, ronReceipt.Resolution.ReceiptStatus);

                for (int i = 0; i < 50 &&
                     session.PlayerSnapshot!.PublicView.Board.Phase != WireGamePhase.RoundEnd; i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }

                Assert.Equal(WireGamePhase.RoundEnd, session.PlayerSnapshot!.PublicView.Board.Phase);
                RoundSettlementCalculation calculation = runtime.ActorForTest.LastSettlementCalculation!;
                Assert.Equal(RoundOutcomeKind.Ron, calculation.Outcome.Kind);
                Assert.Equal(0, calculation.Outcome.WinnerSeat);
                Assert.Equal(0, calculation.Settlement.RemainingRiichiSticks);
                han = calculation.Breakdown!.TotalYakuHan;
                fu = calculation.Breakdown.Fu.FinalFu;
                points = calculation.Breakdown.TotalPoints;
                firstFinalScores = calculation.Settlement.FinalScores.ToArray();
                Assert.Equal(100_000L, firstFinalScores.Sum());

                pumpCts.Cancel();
                await pump;
                LocalMatchSnapshot after = await match.CompleteRoundAsync(ct);
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                Assert.Equal(0, after.RiichiSticks);
                Assert.Equal(firstFinalScores, after.Scores);
                Assert.Single(after.Records);
                Assert.Equal(0, after.Records[0].RemainingRiichiSticks);
            }
            finally
            {
                await match.DisposeAsync();
            }

            LocalMatchCoordinator restored = await LocalMatchCoordinator.OpenAsync(options, ct);
            try
            {
                Assert.Equal(0, restored.Snapshot.RiichiSticks);
                Assert.Equal(firstFinalScores, restored.Snapshot.Scores);
                LocalMatchRound next = await restored.OpenRoundAsync(ct);
                Assert.NotEqual(firstRoundId, next.RoundId);
                await next.Channel.OpenAsync(
                    new TableChannelConnectRequest(next.Ticket, "match-stick-2", null),
                    ct);
                LocalTableRuntime nextRuntime = ((LocalTableChannel)next.Channel).RuntimeForMatch!;
                RoundStateMachine nextRound = nextRuntime.ActorForTest.GetRoundStateForHost();
                Assert.Equal(0, nextRound.RiichiSticks);
                Assert.Equal(firstFinalScores, nextRound.Scores.ToArray());
                ConservationReport report = TilePositionMap.Validate(nextRound.Tiles);
                Assert.True(report.IsValid, string.Join("; ", report.Errors));
                Assert.Equal(136, report.AssignedCount);
                for (int seat = 0; seat < 4; seat++)
                {
                    Assert.False(nextRuntime.ActorForTest.FuritenForHost.IsFuriten(seat));
                }
            }
            finally
            {
                await restored.DisposeAsync();
            }

            Assert.Equal(5, han);
            Assert.Equal(40, fu);
            Assert.Equal(13_000L, points);
            Assert.Equal(new long[] { 37_000L, 13_000L, 25_000L, 25_000L }, firstFinalScores);
        }

        [Fact]
        public async Task MatchLockRejectsASecondLiveCoordinator()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            var options = new LocalMatchHostOptions
            {
                SaveDirectory = Path.Combine(harness.Root, "lock"),
                MatchId = "lock-1",
                Environment = harness.Options.Environment
            };
            LocalMatchCoordinator first = await LocalMatchCoordinator.OpenAsync(options, ct);
            try
            {
                await Assert.ThrowsAsync<LocalTableException>(
                    async () => await LocalMatchCoordinator.OpenAsync(options, ct));
            }
            finally
            {
                await first.DisposeAsync();
            }
        }

        [Fact]
        public async Task CorruptMatchPrimaryFallsBackToPreviousOrIsRejected()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            string root = Path.Combine(harness.Root, "corrupt");
            var options = new LocalMatchHostOptions
            {
                SaveDirectory = root,
                MatchId = "corrupt-1",
                Environment = harness.Options.Environment
            };
            LocalMatchCoordinator first = await LocalMatchCoordinator.OpenAsync(options, ct);
            string path = Path.Combine(root, "corrupt-1.match");
            await first.DisposeAsync();
            Assert.True(File.Exists(path));

            byte[] bytes = File.ReadAllBytes(path);
            bytes[bytes.Length - 1] ^= 0x01;
            File.WriteAllBytes(path, bytes);

            try
            {
                LocalMatchCoordinator restored = await LocalMatchCoordinator.OpenAsync(options, ct);
                try
                {
                    Assert.Equal(4, restored.Snapshot.Scores.Count);
                    Assert.Equal(100_000L, restored.Snapshot.Scores.Sum());
                }
                finally
                {
                    await restored.DisposeAsync();
                }
            }
            catch (LocalTableException exception)
            {
                Assert.Equal(LocalTableErrorKind.CorruptState, exception.ErrorKind);
            }
        }
        [Fact]
        public void MatchCodecPermitsRiichiSticksAndKeepsGoodPreviousCopy()
        {
            string root = Path.Combine(Path.GetTempPath(), "p41-match-codec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "m.match");
                LocalMatchState state = LocalMatchState.Create("m");
                state.Restore(
                    "m",
                    1,
                    1,
                    "m-r1",
                    "m-r1",
                    0,
                    1,
                    0,
                    0,
                    1,
                    new long[] { 24_000L, 25_000L, 25_000L, 25_000L },
                    Array.Empty<long>(),
                    false,
                    string.Empty,
                    new List<RankingEntry>(),
                    new List<LocalMatchRoundRecord>());
                LocalMatchStateCodec.Save(path, state, 0);
                LocalMatchState loaded = LocalMatchStateCodec.Load(path)!;
                Assert.Equal(1, loaded.RiichiSticks);
                Assert.Equal(24_000L, loaded.Scores[0]);

                // Create a good prev at version 2, then corrupt primary twice.
                LocalMatchState second = LocalMatchState.Create("m");
                second.Restore(
                    "m",
                    2,
                    1,
                    "m-r1",
                    "m-r1",
                    0,
                    1,
                    0,
                    0,
                    1,
                    new long[] { 24_000L, 25_000L, 25_000L, 25_000L },
                    Array.Empty<long>(),
                    false,
                    string.Empty,
                    new List<RankingEntry>(),
                    new List<LocalMatchRoundRecord>());
                LocalMatchStateCodec.Save(path, second, 1);
                byte[] corrupt = File.ReadAllBytes(path);
                corrupt[corrupt.Length - 1] ^= 0x01;
                File.WriteAllBytes(path, corrupt);
                Assert.Equal(1, LocalMatchStateCodec.Load(path)!.RiichiSticks);

                corrupt = File.ReadAllBytes(path);
                corrupt[corrupt.Length - 1] ^= 0x01;
                File.WriteAllBytes(path, corrupt);
                Assert.Equal(1, LocalMatchStateCodec.Load(path)!.RiichiSticks);
            }
            finally
            {
                Directory.Delete(root, true);
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

                    bool isMyTurn = snapshot.PublicView.Board.CurrentSeat == 0;
                    bool isResponse = snapshot.PublicView.Board.Phase == WireGamePhase.ResponseWindow ||
                                      snapshot.PublicView.Board.Phase == WireGamePhase.ChankanWindow;
                    if ((isMyTurn || isResponse) && snapshot.PrivateView != null)
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

        private static async Task<PlayerSnapshot> WaitForActionAsync(
            TableSession session,
            ProtocolActionType action,
            CancellationToken ct)
        {
            for (int i = 0; i < 150; i++)
            {
                PlayerSnapshot? snapshot = session.PlayerSnapshot;
                if (snapshot?.PrivateView?.Decision != null &&
                    snapshot.PrivateView.Decision.Actions.Any(
                        a => a.ActionType == (uint)action))
                {
                    return snapshot;
                }

                await Task.Delay(20, ct);
            }

            throw new TimeoutException("Timed out waiting for action " + action + ".");
        }

        private static async Task<TableReceiptResolvedEventArgs> SubmitActionAsync(
            TableSession session,
            PlayerSnapshot snapshot,
            DecisionView decision,
            ProtocolActionType action,
            string requestId,
            uint[]? tiles,
            string? optionId,
            CancellationToken ct)
        {
            var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) =>
            {
                if (string.Equals(args.Command.Key.RequestId.Value, requestId, StringComparison.Ordinal))
                {
                    resolved.TrySetResult(args);
                }
            };
            session.ReceiptResolved += handler;
            try
            {
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    action,
                    optionId: optionId,
                    tileIds: tiles);
                CommandSubmission submission = await session.SubmitAsync(
                    new ProtocolCommand(
                        CommandKind.SubmitAction,
                        payload,
                        new RequestId(requestId),
                        session.Binding!.Scope.ScopeId,
                        new StateVersion(snapshot.StateVersion),
                        new RoundId(snapshot.RoundId),
                        decisionId: new DecisionId(decision.DecisionId),
                        controlEpoch: new ControlEpoch(decision.ControlEpoch)),
                    ct);
                Assert.True(submission.Sent);
                return await LocalHostCommandTests.WithTimeoutAsync(resolved.Task, ct);
            }
            finally
            {
                session.ReceiptResolved -= handler;
            }
        }

        private static RoundStateMachine BuildRiichiWinRound(
            string roundId,
            IReadOnlyList<long> scores,
            int riichiSticks)
        {
            const int Dealer = 0;
            var specific = new Dictionary<int, int>();
            int[] seat0Tiles = { 0, 4, 8, 12, 16, 20, 24, 28, 32, 44, 45, 72, 73, 132 };
            int[] seat0Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            for (int i = 0; i < seat0Slots.Length; i++)
            {
                specific[seat0Slots[i]] = seat0Tiles[i];
            }

            specific[53] = 46;
            var used = new HashSet<int>(specific.Values);
            var remaining = new Queue<int>(Enumerable.Range(0, TileId.Count).Where(id => !used.Contains(id)));
            var order = new int[TileId.Count];
            for (int i = 0; i < TileId.Count; i++)
            {
                order[i] = specific.TryGetValue(i, out int entity) ? entity : remaining.Dequeue();
            }

            RecordedRandomDraw wall = RecordedRandomDraw.Create(
                roundId + "-wall",
                order.Select(id => new TileId(id)).ToArray());
            TilePositionMap dealt = TilePositionMap.CreateDealt(wall, Dealer);
            var spec = new RoundSpec(
                roundId,
                Dealer,
                1,
                0,
                0,
                scores,
                riichiSticks);
            return RoundStateMachine.CreateDealt(spec, dealt, RoundPhase.CheatWindow);
        }
    }
}
