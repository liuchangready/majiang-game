using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Ai;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.Domain.Rules;
using Mahjong.TableRuntime;
using Xunit;
using ContractReceiptStatus = Mahjong.Contracts.ReceiptStatus;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;

namespace Mahjong.LocalHost.Tests
{
    [Collection("LocalHostSerial")]
    public sealed class M103LocalHostAcceptanceTests
    {
        [Fact]
        public async Task RedFiveAndNormalFiveRiichiAndDiscardPreserveDistinctEntitiesAndSubmitCapturedTile()
        {
            // Seat 1 hand: 123m (0,4,8), 123p (36,40,44), 123s (72,76,80), 11s (84,85),
            // plus two normal 5m (16, 17) and one red 5m (19) -> discarding any 5m reaches tenpai!
            int[] seat1Tiles = { 0, 4, 8, 36, 40, 44, 72, 76, 80, 84, 85, 16, 17, 19 };
            RoundStateMachine round = BuildDealtWithSeatHand(seat: 1, seat1Tiles);

            IReadOnlyList<LegalActionOption> candidates = LocalCandidateProvider.DeriveTurnCandidates(
                round, 1, null, null);
            LegalActionOption[] riichiFives = candidates
                .Where(c => c.Kind == LegalActionKind.Riichi && c.Tile.HasValue && c.Tile.Value.TypeIndex == 4)
                .ToArray();
            // Red 5m (19) and one normal 5m (16) are both preserved; duplicate normal 5m (17) is deduplicated.
            Assert.Equal(2, riichiFives.Length);
            Assert.Contains(riichiFives, o => o.Tile == new TileId(16) && o.OptionId == "riichi-16");
            Assert.Contains(riichiFives, o => o.Tile == new TileId(19) && o.OptionId == "riichi-19");

            // All physical copies (16, 17, 19) remain distinct in Discard candidates.
            LegalActionOption[] discardFives = candidates
                .Where(c => c.Kind == LegalActionKind.Discard && c.Tile.HasValue && c.Tile.Value.TypeIndex == 4)
                .ToArray();
            Assert.Equal(3, discardFives.Length);

            // Drive real LocalTableDriver to select red-5m Riichi ("riichi-19") and verify tile 19 is discarded.
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
            harness.Options.InitialRound = round;
            harness.Options.AutoPlay = false;

            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            LocalTableRuntime runtime = room.AuthorityForTest;
            var submitted = new TaskCompletionSource<TableOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.DriverForTest.StrategyForTest = new DelegateStrategy(obs =>
                obs.LegalActions.First(a => a.Kind == LegalActionKind.Riichi && a.Tile == new TileId(19)));
            runtime.DriverForTest.AiSubmitCompletedForTest = (seat, _, result) =>
            {
                if (seat == 1)
                {
                    submitted.TrySetResult(result);
                }
            };

            runtime.DriverForTest.Start();
            TableOperationResult res = await LocalHostCommandTests.WithTimeoutAsync(
                submitted.Task, TestContext.Current.CancellationToken);
            await runtime.DriverForTest.StopAsync();

            Assert.Equal(TableOperationStatus.Accepted, res.Status);
            RoundStateMachine after = runtime.ActorForTest.GetRoundStateForHost();
            Assert.True(after.IsRiichi(1));
            Assert.Contains(after.Events, e => e.Type == RoundEventType.TileDiscarded && e.Seat == 1 && e.Tile == new TileId(19));
            Assert.Equal(136, TilePositionMap.Validate(after.Tiles).AssignedCount);
        }

        [Fact]
        public async Task PonWithRedFiveAndNormalFivesSubmitsExactChosenCombination()
        {
            // Seat 0 discards 5m (18). Seat 1 holds 5m (16), 5m (17), 5m-red (19), plus other tiles.
            // Old LocalTableDriver rescanned hand and always consumed (16, 17), ignoring chosen.Tiles.
            // M1-03 must consume the exact chosen combination (16, 19) when "pon-16-19" is chosen.
            int[] seat0Tiles = { 18, 0, 1, 2, 36, 37, 38, 72, 73, 74, 108, 109, 110, 112 };
            int[] seat1Tiles = { 16, 17, 19, 12, 20, 21, 40, 44, 48, 80, 84, 88, 116 };
            RoundStateMachine round = BuildRoundWithSeatHands(0, seat0Tiles, 1, seat1Tiles);

            // Discard 18 from seat 0 opening response window for seat 1.
            RoundTransition discarded = round.Execute(RoundCommand.Discard(round.RoundId, 0, new TileId(18), new[] { 1 }));
            Assert.True(discarded.IsAccepted);
            round = discarded.State!;

            IReadOnlyList<LegalActionOption> responseCandidates = LocalCandidateProvider.DeriveResponseCandidates(
                round, 1, round.Window!, null, null);
            LegalActionOption[] ponOptions = responseCandidates.Where(c => c.Kind == LegalActionKind.Pon).ToArray();
            Assert.Equal(3, ponOptions.Length);
            LegalActionOption redPon = ponOptions.Single(o => o.OptionId == "pon-16-19");
            Assert.Equal(new[] { new TileId(18), new TileId(16), new TileId(19) }, redPon.Tiles);

            // Also verify multiple Chi combinations for the same discard (4m + 6m[20] vs 4m + 6m[21]).
            LegalActionOption[] chiOptions = responseCandidates.Where(c => c.Kind == LegalActionKind.Chi).ToArray();
            Assert.Equal(2, chiOptions.Length);
            Assert.Contains(chiOptions, o => o.OptionId == "chi-12-20");
            Assert.Contains(chiOptions, o => o.OptionId == "chi-12-21");

            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
            harness.Options.InitialRound = round;
            harness.Options.AutoPlay = false;

            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            LocalTableRuntime runtime = room.AuthorityForTest;
            var ponSubmitted = new TaskCompletionSource<TableOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.DriverForTest.StrategyForTest = new DelegateStrategy(obs =>
                obs.LegalActions.First(a => a.Kind == LegalActionKind.Pon && a.OptionId == "pon-16-19"));
            runtime.DriverForTest.AiSubmitCompletedForTest = (seat, _, result) =>
            {
                if (seat == 1)
                {
                    ponSubmitted.TrySetResult(result);
                }
            };

            runtime.DriverForTest.Start();
            TableOperationResult res = await LocalHostCommandTests.WithTimeoutAsync(
                ponSubmitted.Task, TestContext.Current.CancellationToken);
            await runtime.DriverForTest.StopAsync();

            Assert.Equal(TableOperationStatus.Accepted, res.Status);
            RoundStateMachine after = runtime.ActorForTest.GetRoundStateForHost();
            RoundMeld meld = Assert.Single(after.Melds, m => m.Seat == 1 && m.Kind == RoundMeldKind.Pon);
            Assert.Equal(new TileId(18), meld.CalledTile);
            Assert.Contains(new TileId(16), meld.Tiles);
            Assert.Contains(new TileId(19), meld.Tiles);
            Assert.DoesNotContain(new TileId(17), meld.Tiles);
            Assert.Contains(new TileId(17), after.Tiles.TilesInZone(TileZone.Hand, 1));
            Assert.Equal(136, TilePositionMap.Validate(after.Tiles).AssignedCount);
        }

        [Fact]
        public async Task AiClosedKanOpenKanAddedKanAndChankanRonUseCapturedEntitiesAndSettlement()
        {
            // 1. ClosedKan -> Rinshan -> Tsumo via LocalTableDriver
            int[] closedKanHand = { 0, 1, 2, 3, 36, 40, 44, 48, 52, 56, 60, 64, 68, 72 };
            RoundStateMachine closedKanRound = BuildRoundWithSeatHands(0, closedKanHand, 1, new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, rinshan0: 73);
            await using (var harness = new P41Harness())
            {
                harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                harness.Options.InitialRound = closedKanRound;
                harness.Options.AutoPlay = false;

                await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
                LocalTableRuntime runtime = room.AuthorityForTest;
                var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                runtime.DriverForTest.StrategyForTest = new DelegateStrategy(obs =>
                    obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Tsumo) ??
                    obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Kan && a.OptionId != null && a.OptionId.StartsWith("closed-kan-", StringComparison.Ordinal)) ??
                    obs.LegalActions.First());
                runtime.DriverForTest.AiSubmitCompletedForTest = (_, _, _) =>
                {
                    if (runtime.ActorForTest.GetRoundStateForHost().IsFinished)
                    {
                        finished.TrySetResult(true);
                    }
                };

                runtime.DriverForTest.Start();
                await LocalHostCommandTests.WithTimeoutAsync(finished.Task, TestContext.Current.CancellationToken);
                await runtime.DriverForTest.StopAsync();

                RoundStateMachine after = runtime.ActorForTest.GetRoundStateForHost();
                Assert.True(
                    after.IsFinished,
                    $"Branch1 not finished: wall={after.Tiles.WallRemaining}, phase={after.Phase}, driver={runtime.DriverForTest.DescribeDriverForTest()}");
                Assert.Contains(after.Melds, m => m.Seat == 0 && m.Kind == RoundMeldKind.ClosedKan);
                Assert.Contains(after.Events, e => e.Type == RoundEventType.TsumoWin && e.Seat == 0);
                Assert.Equal(136, TilePositionMap.Validate(after.Tiles).AssignedCount);
            }

            // 2. OpenKan via LocalTableDriver
            int[] seat0Open = { 32, 4, 5, 6, 40, 44, 48, 52, 56, 60, 76, 80, 84, 88 };
            int[] seat1Open = { 33, 34, 35, 8, 12, 16, 64, 68, 92, 96, 100, 108, 112 };
            RoundStateMachine openKanRound = BuildRoundWithSeatHands(0, seat0Open, 1, seat1Open);
            openKanRound = openKanRound.Execute(RoundCommand.Discard(openKanRound.RoundId, 0, new TileId(32), new[] { 1 })).State!;
            await using (var harness = new P41Harness())
            {
                harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                harness.Options.InitialRound = openKanRound;
                harness.Options.AutoPlay = false;

                await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
                LocalTableRuntime runtime = room.AuthorityForTest;
                var kanDone = new TaskCompletionSource<TableOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                runtime.DriverForTest.StrategyForTest = new DelegateStrategy(obs =>
                    obs.LegalActions.First(a => a.Kind == LegalActionKind.Kan && a.OptionId != null && a.OptionId.StartsWith("open-kan-", StringComparison.Ordinal)));
                runtime.DriverForTest.AiSubmitCompletedForTest = (seat, _, result) =>
                {
                    if (seat == 1)
                    {
                        kanDone.TrySetResult(result);
                    }
                };

                runtime.DriverForTest.Start();
                TableOperationResult res = await LocalHostCommandTests.WithTimeoutAsync(kanDone.Task, TestContext.Current.CancellationToken);
                await runtime.DriverForTest.StopAsync();

                Assert.Equal(TableOperationStatus.Accepted, res.Status);
                RoundStateMachine after = runtime.ActorForTest.GetRoundStateForHost();
                Assert.Contains(after.Melds, m => m.Seat == 1 && m.Kind == RoundMeldKind.OpenKan);
                Assert.Equal(136, TilePositionMap.Validate(after.Tiles).AssignedCount);
            }

            // 3. AddedKan + Chankan Ron via LocalTableDriver (driven from fresh CreateDealt so journal history matches domain)
            RoundStateMachine chankanSetup = BuildChankanStartRound();
            await using (var harness = new P41Harness())
            {
                harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                harness.Options.InitialRound = chankanSetup;
                harness.Options.AutoPlay = false;
                harness.Options.Ai.AllowCheats = false;

                await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
                LocalTableRuntime runtime = room.AuthorityForTest;
                var won = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var trace = new List<string>();
                runtime.DriverForTest.StrategyForTest = new DelegateStrategy(obs =>
                    obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Ron) ??
                    obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Kan && a.OptionId != null && a.OptionId.StartsWith("added-kan-", StringComparison.Ordinal)) ??
                    (obs.Seat == 0 ? obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Pon) : null) ??
                    obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Pass) ??
                    (obs.Seat == 2 ? obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Discard && a.Tile == new TileId(2)) : null) ??
                    (obs.Seat == 0 ? obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Discard && a.Tile == new TileId(124)) : null) ??
                    (obs.MyDrawnTileId.HasValue ? obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Discard && a.Tile == obs.MyDrawnTileId.Value) : null) ??
                    obs.LegalActions.First());
                runtime.DriverForTest.AiSubmitCompletedForTest = (seat, obs, result) =>
                {
                    trace.Add($"s{seat}@{obs.Phase}:{result.Status}/{result.ReasonCode}:{result.Detail}");
                    if (runtime.ActorForTest.GetRoundStateForHost().IsFinished)
                    {
                        won.TrySetResult(true);
                    }
                };

                runtime.DriverForTest.Start();
                await Task.WhenAny(won.Task, Task.Delay(5000, TestContext.Current.CancellationToken));
                await runtime.DriverForTest.StopAsync();

                RoundStateMachine after = runtime.ActorForTest.GetRoundStateForHost();
                Assert.True(
                    after.IsFinished,
                    $"Chankan did not finish: phase={after.Phase}, seat={after.CurrentSeat}, trace=[{string.Join(" | ", trace)}]");
                Assert.Contains(after.Events, e => e.Type == RoundEventType.RonWin && e.Seat == 1);
                Assert.Equal(136, TilePositionMap.Validate(after.Tiles).AssignedCount);
            }
        }

        [Fact]
        public async Task DrawBranchesCoverEmptyWallExhaustiveDrawAutomatedTakeoverDrawAndHumanConfirmDraw()
        {
            // Branch 1: Genuine wall exhaustion through LocalTableDriver -> AI triggers ExhaustiveDraw settlement
            await using (var harness = new P41Harness())
            {
                harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                harness.Options.InitialRound = DealtRound("round-empty-wall");
                harness.Options.AutoPlay = false;
                harness.Options.Ai.AllowCheats = false;

                await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
                LocalTableRuntime runtime = room.AuthorityForTest;
                runtime.DriverForTest.StrategyForTest = new DelegateStrategy(obs =>
                    obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Pass) ??
                    (obs.MyDrawnTileId.HasValue
                        ? obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Discard && a.Tile == obs.MyDrawnTileId.Value)
                        : null) ??
                    obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Discard) ??
                    obs.LegalActions.First());
                runtime.DriverForTest.Start();
                for (int i = 0; i < 400 && !runtime.ActorForTest.GetRoundStateForHost().IsFinished; i++)
                {
                    await Task.Delay(25, TestContext.Current.CancellationToken);
                }

                await runtime.DriverForTest.StopAsync();
                RoundStateMachine after = runtime.ActorForTest.GetRoundStateForHost();
                Assert.True(
                    after.IsFinished,
                    $"Branch1 not finished: wall={after.Tiles.WallRemaining}, phase={after.Phase}, driver={runtime.DriverForTest.DescribeDriverForTest()}");
                Assert.Contains(after.Events, e => e.Type == RoundEventType.ExhaustiveDraw);
                Assert.Equal(136, TilePositionMap.Validate(after.Tiles).AssignedCount);
            }

            // Branch 2: Seat 0 has ConfirmSeatZeroDraw = true AND is Takeover (_aiSeats[0] == true)
            // -> LocalTableDriver must submit direct Draw without opening human PendingDecision(Draw).
            // Start with dealer = 3 so seat 0 holds 13 tiles and enters Draw naturally after seat 3 discards.
            RoundStateMachine seat0DrawRound = RoundStateMachine.CreateDealt(
                new RoundSpec("round-takeover-draw", 3, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0, confirmSeatZeroDraw: true),
                TilePositionMap.CreateDealt(RecordedRandomDraw.Standard("round-takeover-draw"), 3),
                RoundPhase.CheatWindow);
            await using (var harness = new P41Harness())
            {
                harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(new[] { 0 });
                harness.Options.ConfirmSeatZeroDraw = true;
                harness.Options.InitialRound = seat0DrawRound;
                harness.Options.AutoPlay = false;
                harness.Options.Ai.AllowCheats = false;

                ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, TestContext.Current.CancellationToken);
                string channelTicket = LocalTableHost.IssueTicket(harness.Options);
                await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "m103-branch2", null), TestContext.Current.CancellationToken);
                var session = new TableSession(channel, new InMemoryOutboxStore());
                string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
                await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "m103-branch2"), TestContext.Current.CancellationToken);
                for (int i = 0; i < 20 && session.State != TableConnectionState.Ready; i++)
                {
                    await session.PumpOnceAsync(TestContext.Current.CancellationToken);
                }

                try
                {
                    LocalTableRuntime runtime = ((LocalTableChannel)channel).RuntimeForMatch!;
                    Assert.Equal(
                        TableControlStatus.Changed,
                        (await runtime.ActorForTest.RequestTakeoverAsync(
                            0, new OwnerEpoch(1), new ControlEpoch(0), "takeover-draw", TestContext.Current.CancellationToken)).Status);

                    bool seat0OpenedDrawDecision = false;
                    runtime.DriverForTest.StrategyForTest = new DelegateStrategy(obs =>
                    {
                        if (obs.Seat == 0 && obs.LegalActions.Any(a => a.Kind == LegalActionKind.Draw))
                        {
                            seat0OpenedDrawDecision = true;
                        }

                        return obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Pass) ??
                               obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Discard) ??
                               obs.LegalActions.First();
                    });
                    runtime.DriverForTest.Start();
                    for (int i = 0; i < 200 && !runtime.ActorForTest.GetRoundStateForHost().Events.Any(e => e.Type == RoundEventType.TileDrawn && e.Seat == 0); i++)
                    {
                        await Task.Delay(20, TestContext.Current.CancellationToken);
                    }
                    await runtime.DriverForTest.StopAsync();

                    RoundStateMachine after = runtime.ActorForTest.GetRoundStateForHost();
                    Assert.False(seat0OpenedDrawDecision);
                    Assert.Contains(after.Events, e => e.Type == RoundEventType.TileDrawn && e.Seat == 0);
                }
                finally
                {
                    await session.CloseAsync("TEST_DONE", CancellationToken.None);
                    await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
                }
            }

            // Branch 3: Seat 0 is LocalHuman with ConfirmSeatZeroDraw = true -> opens PendingDecision(Draw)
            await using (var harness = new P41Harness())
            {
                harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(new[] { 0 });
                harness.Options.ConfirmSeatZeroDraw = true;
                harness.Options.InitialRound = RoundStateMachine.CreateDealt(
                    new RoundSpec("round-human-draw", 3, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0, confirmSeatZeroDraw: true),
                    TilePositionMap.CreateDealt(RecordedRandomDraw.Standard("round-human-draw"), 3),
                    RoundPhase.CheatWindow);
                harness.Options.AutoPlay = false;
                harness.Options.Ai.AllowCheats = false;

                ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, TestContext.Current.CancellationToken);
                string channelTicket = LocalTableHost.IssueTicket(harness.Options);
                await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "m103-branch3", null), TestContext.Current.CancellationToken);
                var session = new TableSession(channel, new InMemoryOutboxStore());
                string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
                await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "m103-branch3"), TestContext.Current.CancellationToken);
                for (int i = 0; i < 20 && session.State != TableConnectionState.Ready; i++)
                {
                    await session.PumpOnceAsync(TestContext.Current.CancellationToken);
                }

                try
                {
                    LocalTableRuntime runtime = ((LocalTableChannel)channel).RuntimeForMatch!;
                    runtime.DriverForTest.StrategyForTest = new DelegateStrategy(obs =>
                        obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Pass) ??
                        obs.LegalActions.FirstOrDefault(a => a.Kind == LegalActionKind.Discard) ??
                        obs.LegalActions.First());
                    runtime.DriverForTest.Start();
                    for (int i = 0; i < 200 && runtime.ActorForTest.GetPendingDecisionId(0) == null; i++)
                    {
                        await Task.Delay(20, TestContext.Current.CancellationToken);
                    }
                    await runtime.DriverForTest.StopAsync();

                    string? decId = runtime.ActorForTest.GetPendingDecisionId(0);
                    Assert.NotNull(decId);
                    Assert.Contains(
                        runtime.ActorForTest.GetPendingDecisionCandidates(0),
                        o => o.Kind == LegalActionKind.Draw);
                }
                finally
                {
                    await session.CloseAsync("TEST_DONE", CancellationToken.None);
                    await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
                }
            }
        }

        [Fact]
        public async Task TamperedAiDecisionDeadlineIsRejectedWithDecisionMismatchWithoutMutation()
        {
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(new[] { 0 });
            harness.Options.AutoPlay = false;

            await using LocalTableRuntime runtime = await LocalTableRuntime.OpenAsync(harness.Options, TestContext.Current.CancellationToken);
            RoundStateMachine round = runtime.ActorForTest.GetRoundStateForHost();
            // Discard from seat 0 using a host-issued scope so seat 1 opens a decision, then test seat 1 AI capture.
            ulong now = (ulong)Math.Max(0L, harness.Clock.UtcNowMs);
            TableScopeRecord scope0 = runtime.StoreForTest.IssueScope(
                "local-" + P41Harness.RoomId + "-seat0", TableScopeRole.Player, P41Harness.RoomId, now, now + 600_000UL, "scope-0");
            TableScopeRecord scope1 = runtime.StoreForTest.IssueScope(
                "local-" + P41Harness.RoomId + "-seat1", TableScopeRole.Player, P41Harness.RoomId, now, now + 600_000UL, "scope-1");
            TileId s0Discard = round.Tiles.TilesInZone(TileZone.Hand, 0)[0];
            TableOperationResult s0Res = await runtime.ActorForTest.SubmitAsync(
                new TableCommandEnvelope(
                    RoundCommand.Discard(round.RoundId, 0, s0Discard, Array.Empty<int>()),
                    new RequestId("s0-d"),
                    new StateVersion(0),
                    new OwnerEpoch(1),
                    new ControlEpoch(0),
                    null,
                    0L,
                    scope0.ScopeId,
                    "ROUND",
                    TablePayloadSha256.Compute(Encoding.UTF8.GetBytes("s0-d"))),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, s0Res.Status);

            RoundStateMachine s1Round = runtime.ActorForTest.GetRoundStateForHost();
            IReadOnlyList<LegalActionOption> s1Cands = LocalCandidateProvider.DeriveTurnCandidates(s1Round, 1, null, null);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await runtime.ActorForTest.OpenDecisionAsync(
                    new PendingDecision(new DecisionKey(s1Round.RoundId, 1, "dec-s1"), 0L, s1Cands, s1Round.Tiles.DrawnTile?.Tile),
                    TestContext.Current.CancellationToken)).Status);

            TableAiObservationResult cap = await runtime.ActorForTest.CaptureAiObservationAsync(1, TestContext.Current.CancellationToken);
            Assert.True(cap.IsCaptured);
            AiObservation obs = cap.Observation!;
            LegalActionOption discardOpt = obs.LegalActions.First(a => a.Kind == LegalActionKind.Discard);

            // Tamper decisionDeadlineMillis -> DecisionMismatch, zero side effects.
            TableOperationResult tampered = await runtime.ActorForTest.SubmitAsync(
                new TableCommandEnvelope(
                    RoundCommand.Discard(obs.RoundId, 1, discardOpt.Tile!.Value, Array.Empty<int>()),
                    new RequestId("tampered-ai-deadline"),
                    new StateVersion((ulong)obs.StateVersion),
                    cap.OwnerEpoch,
                    new ControlEpoch(unchecked((ulong)obs.ControlEpoch)),
                    new DecisionId(obs.DecisionKey.DecisionId),
                    cap.RecoveryGeneration,
                    scope1.ScopeId,
                    "AI_DISCARD",
                    TablePayloadSha256.Compute(Encoding.UTF8.GetBytes("tampered")),
                    obs.DecisionDeadlineMillis + 1L),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, tampered.Status);
            Assert.Equal(TableRuntimeReasonCode.DecisionMismatch, tampered.ReasonCode);
            Assert.Equal(1UL, (await runtime.ActorForTest.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);
        }

        [Fact]
        public async Task DriverResolvesAlreadyPaidOfferViaSafeDefaultWhenCheatsDisabledOrTakeover()
        {
            RoundStateMachine seat1Round = RoundStateMachine.CreateDealt(
                new RoundSpec("round-driver-paid-fallback", 1, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0),
                TilePositionMap.CreateDealt(RecordedRandomDraw.Standard("round-driver-paid-fallback"), 1),
                RoundPhase.CheatWindow);
            TileId first = seat1Round.Tiles.DrawnTile!.Value.Tile;
            TileId second = seat1Round.Tiles.TileAt(TilePosition.Wall(seat1Round.Tiles.WallPointer));
            int turn = seat1Round.Events.Count(e => e.Type == RoundEventType.TileDrawn || e.Type == RoundEventType.RinshanDrawn);
            var offer = new SleeveBloomOffer(
                "paid-fallback-offer", 1, Mahjong.Domain.Cheat.SleeveBloomDrawKind.Normal, first, second, turn, SleeveBloomOfferMode.Immediate);

            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
            harness.Options.InitialRound = seat1Round;
            harness.Options.AutoPlay = false;
            // Cheats disabled in AI policy: driver must still safely resolve already-paid offer via IsSafeDefault!
            harness.Options.Ai = new LocalAiOptions
            {
                ProfileId = LocalAiProfiles.RandomCheatBasic,
                AllowCheats = false,
                TakeoverMaySpend = false
            };

            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            LocalTableRuntime runtime = room.AuthorityForTest;
            var cheatField = typeof(TableActor).GetField("_cheat", BindingFlags.Instance | BindingFlags.NonPublic)!;
            CheatState cheat = ((CheatState)cheatField.GetValue(runtime.ActorForTest)!)
                .WithSleeveBloom(new SleeveBloomState().WithImmediateOffer(offer).WithUsedOnTurn(turn));
            cheatField.SetValue(runtime.ActorForTest, cheat);

            Assert.True(runtime.ActorForTest.HasActiveCheatOffer(1));
            runtime.DriverForTest.Start();
            for (int i = 0; i < 80 && runtime.ActorForTest.HasActiveCheatOffer(1); i++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            await runtime.DriverForTest.StopAsync();

            Assert.False(runtime.ActorForTest.HasActiveCheatOffer(1));
            Assert.Equal(136, TilePositionMap.Validate(runtime.ActorForTest.GetRoundStateForHost().Tiles).AssignedCount);
        }

        [Fact]
        public async Task DurableCheatRequestReconciliationCoversQueryReplayConflictAndUnknownRetry()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(new[] { 0 });
            CancellationToken ct = TestContext.Current.CancellationToken;

            await using LocalTableRuntime runtime = await LocalTableRuntime.OpenAsync(harness.Options, ct);
            ulong now = (ulong)Math.Max(0L, harness.Clock.UtcNowMs);
            TableScopeRecord scope = runtime.StoreForTest.IssueScope(
                "local-" + P41Harness.RoomId + "-seat0",
                TableScopeRole.Player,
                P41Harness.RoomId,
                now,
                now + 600_000UL,
                "cheat-reconcile-scope");

            RoundStateMachine round = runtime.ActorForTest.GetRoundStateForHost();
            TileId[] markTiles = round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray();
            var requestId = new RequestId("cheat-rec-1");
            const string api = "CHEAT";
            byte[] canonicalBytes = Encoding.UTF8.GetBytes("tile-mark|" + string.Join(",", markTiles.Select(t => t.Value)));
            TablePayloadSha256 digest = TablePayloadSha256.Compute(canonicalBytes);

            var command = new RoundCheatCommand(
                round.RoundId, 0, RoundCheatAction.SubmitTileMark, requestId.Value,
                RoundCheatPayload.TileMark("grp-rec-1", markTiles));
            var envelope = new TableCheatCommandEnvelope(
                command,
                requestId,
                new StateVersion(0),
                new OwnerEpoch(1),
                new ControlEpoch(0),
                null,
                0L,
                scope.ScopeId,
                api,
                digest);

            // 1. Original commit succeeds and charges once.
            TableOperationResult first = await runtime.ActorForTest.SubmitCheatAsync(envelope, ct);
            Assert.Equal(TableOperationStatus.Accepted, first.Status);
            int balanceAfter = runtime.ActorForTest.GetCheatLedgerForHost().Balances[0];
            Assert.Equal(119, balanceAfter);

            // 2. QueryReceiptAsync returns Found with original Applied receipt.
            ReceiptLookupResult query = await runtime.ActorForTest.QueryReceiptAsync(
                scope.AccountId, scope.ScopeId, requestId, ct);
            Assert.Equal(ReceiptLookupStatus.Found, query.Status);
            Assert.NotNull(query.Receipt);
            Assert.Equal(ContractReceiptStatus.Applied, query.Receipt!.Status);

            // 3. ReceiptLookupResult Unknown / NotFound never proves uncommitted -> caller retains original RequestId & bytes.
            var unknown = ReceiptLookupResult.Unknown();
            var notFound = ReceiptLookupResult.NotFound();
            Assert.False(unknown.ProvesNotCommitted);
            Assert.False(notFound.ProvesNotCommitted);

            // Retry with exact same RequestId, ScopeId, Api and canonical digest -> FromReplay (0 extra charge).
            TableOperationResult replay = await runtime.ActorForTest.SubmitCheatAsync(envelope, ct);
            Assert.Equal(TableOperationStatus.Replayed, replay.Status);
            Assert.Equal(balanceAfter, runtime.ActorForTest.GetCheatLedgerForHost().Balances[0]);
            Assert.Single(runtime.ActorForTest.GetCheatLedgerForHost().Entries);

            // 4. Same RequestId with altered payload digest -> Conflict (0 side effects).
            var altered = new TableCheatCommandEnvelope(
                command,
                requestId,
                new StateVersion(0),
                new OwnerEpoch(1),
                new ControlEpoch(0),
                null,
                0L,
                scope.ScopeId,
                api,
                TablePayloadSha256.Compute(Encoding.UTF8.GetBytes("altered-payload")));
            TableOperationResult conflict = await runtime.ActorForTest.SubmitCheatAsync(altered, ct);
            Assert.Equal(TableOperationStatus.Conflict, conflict.Status);
            Assert.Equal(balanceAfter, runtime.ActorForTest.GetCheatLedgerForHost().Balances[0]);
            Assert.Equal(136, TilePositionMap.Validate(runtime.ActorForTest.GetRoundStateForHost().Tiles).AssignedCount);
        }

        private static RoundStateMachine DealtRound(string roundId) =>
            RoundStateMachine.CreateDealt(
                new RoundSpec(roundId, 0, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0),
                TilePositionMap.CreateDealt(RecordedRandomDraw.Standard(roundId), 0),
                RoundPhase.CheatWindow);

        private static RoundStateMachine BuildDealtWithSeatHand(int seat, int[] seatTiles)
        {
            var used = new HashSet<int>(seatTiles);
            var remaining = new Queue<int>(Enumerable.Range(0, TileId.Count).Where(id => !used.Contains(id)));
            var order = new int[TileId.Count];
            // Fill hands via standard dealing slots for the target seat:
            // Seat 0 draws slots 0..3,16..19,32..35,48,52; Seat 1: 4..7,20..23,36..39,49; etc.
            // Using TilePositionMap.CreateDealt(wall, seat) puts seat's 14 tiles at seat's deal positions:
            // For dealer == seat, dealer gets 14 tiles at (0..3,16..19,32..35,48,52).
            int[] dealerSlots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            var specific = new Dictionary<int, int>();
            for (int i = 0; i < dealerSlots.Length; i++)
            {
                specific[dealerSlots[i]] = seatTiles[i];
            }

            for (int i = 0; i < TileId.Count; i++)
            {
                order[i] = specific.TryGetValue(i, out int val) ? val : remaining.Dequeue();
            }

            var tiles = order.Select(id => new TileId(id)).ToArray();
            TilePositionMap dealt = TilePositionMap.CreateDealt(RecordedRandomDraw.Create("m103-wall", tiles), seat);
            return RoundStateMachine.CreateDealt(
                new RoundSpec("round-m103-" + seat, seat, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0),
                dealt,
                RoundPhase.CheatWindow);
        }

        private static RoundStateMachine BuildRoundWithSeatHands(
            int dealer,
            int[] dealerTiles,
            int seat1,
            int[] seat1Tiles,
            int? rinshan0 = null)
        {
            var specific = new Dictionary<int, int>();
            int[] dealerSlots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            int[] seat1Slots = { 4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 49 };
            for (int i = 0; i < dealerSlots.Length; i++)
            {
                specific[dealerSlots[i]] = dealerTiles[i];
            }

            for (int i = 0; i < seat1Slots.Length; i++)
            {
                specific[seat1Slots[i]] = seat1Tiles[i];
            }

            if (rinshan0.HasValue)
            {
                specific[122] = rinshan0.Value;
            }

            var used = new HashSet<int>(specific.Values);
            var remaining = new Queue<int>(Enumerable.Range(0, TileId.Count).Where(id => !used.Contains(id)));
            var order = new int[TileId.Count];
            for (int i = 0; i < TileId.Count; i++)
            {
                order[i] = specific.TryGetValue(i, out int val) ? val : remaining.Dequeue();
            }

            TilePositionMap dealt = TilePositionMap.CreateDealt(
                RecordedRandomDraw.Create("m103-hands", order.Select(id => new TileId(id)).ToArray()), dealer);
            return RoundStateMachine.CreateDealt(
                new RoundSpec("round-m103-hands", dealer, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0),
                dealt,
                RoundPhase.CheatWindow);
        }

        private static RoundStateMachine BuildChankanStartRound()
        {
            int[] seat0Tiles = { 0, 1, 36, 40, 44, 72, 76, 80, 108, 112, 116, 120, 124 };
            int[] seat1Tiles = { 4, 8, 17, 21, 25, 41, 45, 49, 89, 93, 97, 104, 105 };
            int[] seat2Start = { 2, 12, 16, 20, 24, 52, 56, 60, 64, 68, 73, 77, 81, 130 };
            var specific = new Dictionary<int, int>();
            int[] s2Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            int[] s0Slots = { 8, 9, 10, 11, 24, 25, 26, 27, 40, 41, 42, 43, 50 };
            int[] s1Slots = { 12, 13, 14, 15, 28, 29, 30, 31, 44, 45, 46, 47, 51 };
            for (int i = 0; i < s2Slots.Length; i++) specific[s2Slots[i]] = seat2Start[i];
            for (int i = 0; i < s0Slots.Length; i++) specific[s0Slots[i]] = seat0Tiles[i];
            for (int i = 0; i < s1Slots.Length; i++) specific[s1Slots[i]] = seat1Tiles[i];
            specific[53] = 125;
            specific[54] = 126;
            specific[55] = 127;
            specific[56] = 3; // 4th 1m drawn by seat 0
            var used = new HashSet<int>(specific.Values);
            var remaining = new Queue<int>(Enumerable.Range(0, TileId.Count).Where(id => !used.Contains(id)));
            var order = new int[TileId.Count];
            for (int i = 0; i < TileId.Count; i++) order[i] = specific.TryGetValue(i, out int val) ? val : remaining.Dequeue();

            TilePositionMap dealt = TilePositionMap.CreateDealt(
                RecordedRandomDraw.Create("chankan-wall", order.Select(id => new TileId(id)).ToArray()), 2);
            return RoundStateMachine.CreateDealt(
                new RoundSpec("round-chankan", 2, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0),
                dealt,
                RoundPhase.CheatWindow);
        }

        private sealed class DelegateStrategy : IAiStrategy
        {
            private readonly Func<AiObservation, LegalActionOption> _choose;

            public DelegateStrategy(Func<AiObservation, LegalActionOption> choose)
            {
                _choose = choose;
            }

            public string AlgorithmId => "test-delegate";

            public int AlgorithmVersion => 1;

            public AiStrategicChoice Decide(AiObservation observation, CancellationToken cancellationToken)
            {
                return new AiStrategicChoice(_choose(observation), 1.0d);
            }
        }
    }
}
