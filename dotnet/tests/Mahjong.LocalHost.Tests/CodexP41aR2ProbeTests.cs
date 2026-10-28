using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Rules;
using Mahjong.TableRuntime;
using Xunit;
using DecisionView = Mahjong.Contracts.Wire.DecisionView;
using GamePhase = Mahjong.Contracts.Wire.GamePhase;
using LegalActionView = Mahjong.Contracts.Wire.LegalActionView;
using MeldType = Mahjong.Contracts.Wire.MeldType;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;

namespace Mahjong.LocalHost.Tests
{
    public sealed class CodexP41aR2ProbeTests
    {
        [Fact]
        public void CandidateEntitiesSurviveTimingCheckpoint()
        {
            var tiles = new[] { new TileId(8), new TileId(0), new TileId(4) };
            var option = new LegalActionOption(LegalActionKind.Chi, 0, tiles[0], "chi-0-4", tiles);
            var decision = new TableRecoveredDecision(new DecisionKey("r", 0, "d"), 1,
                new[] { option }, null, 3000, 20000, 0);
            var checkpoint = new TableTimingCheckpoint("r", new long[] { 20000, 20000, 20000, 20000 }, new[] { decision });
            var recovered = TableTimingCheckpointCodec.Decode(TableTimingCheckpointCodec.Encode(checkpoint));
            Assert.Equal(option.Tiles.Select(t => t.Value), recovered.Decisions[0].Candidates[0].Tiles.Select(t => t.Value));
        }

        [Fact]
        public void ChankanOnlyYakuEligibilityUsesProspectiveContext()
        {
            int[] hand = { 4, 8, 48, 52, 56, 96, 100, 104, 40, 44, 49, 108, 109, 132 };
            int[] slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            RoundStateMachine round = BuildRoundWithAssignedSlots(slots, hand);
            var discarded = round.Execute(RoundCommand.Discard(round.RoundId, 0, new TileId(132), Array.Empty<int>()));
            Assert.True(discarded.IsAccepted);
            round = discarded.State!;
            var closed = round.Tiles.TilesInZone(TileZone.Hand, 0);
            var player = new PlayerHand(closed);
            Assert.Null(WinningScoreEvaluator.Evaluate(player, new WinContext(new TileId(3), false, true, 1, 1)));
            Assert.NotNull(WinningScoreEvaluator.Evaluate(player, new WinContext(new TileId(3), false, true, 1, 1, isChankan: true)));
            Assert.Contains(0, LocalCandidateProvider.DeriveEligibleSeatsForAddedKan(round, 1, new TileId(3), new bool[4], new bool[4]));
        }

        [Fact]
        public async Task ChankanAsOnlyYakuMustReachTerminal()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Seat 0 hand starts in Iishanten:
            // 2m (4), 3m (8) -> needs 1m or 4m
            // 5-6-7m (16, 20, 24)
            // 2-3-4p (40, 44, 48)
            // 2-3s (76, 80) -> needs 1s or 4s
            // 8-8s (100, 101) -> pair
            // 130 -> dummy tile to discard on turn 2
            // 132 -> dummy tile to discard on turn 1
            int[] seat0Tiles = { 4, 8, 48, 52, 56, 96, 100, 40, 44, 49, 108, 109, 130, 132 };
            int[] dealerSlots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            var specific = new Dictionary<int, int>();
            for (int i = 0; i < dealerSlots.Length; i++)
            {
                specific[dealerSlots[i]] = seat0Tiles[i];
            }

            // Seat 1 hand has two 1m tiles (0 and 1)
            specific[4] = 0;
            specific[5] = 1;

            // Draws:
            specific[53] = 120; // Seat 1 turn 1 draw -> discards 120
            specific[54] = 2;   // Seat 2 turn 1 draw -> tile 2 (1m) -> Seat 2 discards 2! Seat 1 calls Pon on 2! (Seat 0 cannot Ron because Seat 0 is in Iishanten)
            specific[55] = 121; // Seat 2 turn 2 draw -> discards 121
            specific[56] = 122; // Seat 3 turn 2 draw -> discards 122
            specific[57] = 104;  // Seat 0 turn 2 draw -> tile 84 (4s) completes 2-3-4s! Seat 0 enters Tenpai waiting on 1m/4m! Discards 130!
            specific[58] = 3;   // Seat 1 turn 2 draw -> tile 3 (4th 1m) -> Seat 1 calls AddedKan! Chankan window opens!

            harness.Options.InitialRound = BuildRoundWithSpecificSlots(specific);

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(25));
                Task pump = StartPump(session, pumpCts.Token);

                // 1. Seat 0 discards 132
                PlayerSnapshot snapshot = await WaitForDecisionAsync(session, pumpCts.Token);
                DecisionView decision = snapshot.PrivateView.Decision!;

                var discardResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-discard-132")
                    {
                        discardResolved.TrySetResult(args);
                    }
                };

                BusinessPayload discardPayload = CommandPayloadFactory.SubmitAction(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    ProtocolActionType.DiscardTile,
                    tileIds: new uint[] { 132u });
                var discardCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    discardPayload,
                    new RequestId("seat0-discard-132"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snapshot.StateVersion),
                    new RoundId(snapshot.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));

                await session.SubmitAsync(discardCommand, pumpCts.Token);
                await LocalHostCommandTests.WithTimeoutAsync(discardResolved.Task, pumpCts.Token);

                // 2. Seat 0 draws tile 84 (4s) on turn 2, completing 2-3-4s (reaches Tenpai), discards 130
                PlayerSnapshot turn2Snapshot = await WaitForActionTypeAsync(
                    session,
                    ProtocolActionType.DiscardTile,
                    pumpCts.Token);
                DecisionView turn2Decision = turn2Snapshot.PrivateView.Decision!;

                var discard2Resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-discard-130")
                    {
                        discard2Resolved.TrySetResult(args);
                    }
                };

                BusinessPayload discard2Payload = CommandPayloadFactory.SubmitAction(
                    turn2Snapshot.RoundId,
                    turn2Decision.DecisionId,
                    turn2Decision.ControlEpoch,
                    ProtocolActionType.DiscardTile,
                    tileIds: new uint[] { 130u });
                var discard2Command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    discard2Payload,
                    new RequestId("seat0-discard-130"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(session.PlayerSnapshot!.StateVersion),
                    new RoundId(turn2Snapshot.RoundId),
                    decisionId: new DecisionId(turn2Decision.DecisionId),
                    controlEpoch: new ControlEpoch(turn2Decision.ControlEpoch));

                await session.SubmitAsync(discard2Command, pumpCts.Token);
                await LocalHostCommandTests.WithTimeoutAsync(discard2Resolved.Task, pumpCts.Token);

                // 3. Seat 1 draws tile 3 (1m) and declares AddedKan!
                // Seat 0 receives DeclareRon in ChankanWindow!
                PlayerSnapshot ronSnapshot = await WaitForActionTypeAsync(
                    session,
                    ProtocolActionType.DeclareRon,
                    pumpCts.Token);
                await Task.Delay(150, pumpCts.Token);
                ronSnapshot = session.PlayerSnapshot!;
                Assert.NotNull(ronSnapshot.PrivateView.Decision);
                DecisionView ronDecision = ronSnapshot.PrivateView.Decision;

                var ronResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-chankan-ron-1")
                    {
                        ronResolved.TrySetResult(args);
                    }
                };

                BusinessPayload ronPayload = CommandPayloadFactory.SubmitAction(
                    ronSnapshot.RoundId,
                    ronDecision.DecisionId,
                    ronDecision.ControlEpoch,
                    ProtocolActionType.DeclareRon);
                var ronCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    ronPayload,
                    new RequestId("seat0-chankan-ron-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(session.PlayerSnapshot!.StateVersion),
                    new RoundId(ronSnapshot.RoundId),
                    decisionId: new DecisionId(ronDecision.DecisionId),
                    controlEpoch: new ControlEpoch(ronDecision.ControlEpoch));

                CommandSubmission ronSub = await session.SubmitAsync(ronCommand, pumpCts.Token);
                Assert.True(ronSub.Sent);

                TableReceiptResolvedEventArgs ronReceipt;
                try
                {
                    ronReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                        ronResolved.Task,
                        pumpCts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new Exception($"ronResolved timed out! LastSettlementResult={LocalTableRuntime.LastSettlementResult?.Status}: {LocalTableRuntime.LastSettlementResult?.ReasonCode} {LocalTableRuntime.LastSettlementResult?.Detail}, LastCommandException={LocalTableRuntime.LastCommandException}");
                }

                Assert.True(
                    ronReceipt.Resolution.ReceiptStatus == ReceiptStatus.Applied,
                    "ron rejected: " + ronReceipt.Resolution.ReasonCode);

                for (int i = 0; i < 20 &&
                     session.PlayerSnapshot!.PublicView.Board.Phase != GamePhase.RoundEnd; i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }

                Assert.Equal(GamePhase.RoundEnd, session.PlayerSnapshot!.PublicView.Board.Phase);

                RoundSettlementCalculation? chankanCalc = ((LocalTableChannel)channel).RuntimeForMatch?.ActorForTest.LastSettlementCalculation;
                Assert.NotNull(chankanCalc);
                Assert.Equal(RoundOutcomeKind.Ron, chankanCalc.Outcome.Kind);
                Assert.Equal(0, chankanCalc.Outcome.WinnerSeat);
                Assert.Equal(1, chankanCalc.Outcome.LoserSeat);
                Assert.NotNull(chankanCalc.Breakdown);
                Assert.Contains(chankanCalc.Breakdown.Yaku, y => y.Yaku.Code == "CHANKAN");
                Assert.True(chankanCalc.Breakdown.TotalPoints > 0);
                Assert.True(chankanCalc.Payments.Deltas[0] > 0);
                Assert.True(chankanCalc.Payments.Deltas[1] < 0);

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        private static Task StartPump(TableSession session, CancellationToken token)
        {
            return Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await session.PumpOnceAsync(token);
                    }
                }
                catch (Exception ex) when (
                    ex is OperationCanceledException ||
                    ex is TableChannelClosedException ||
                    ex is InvalidOperationException)
                {
                }
            }, token);
        }

        private static async Task<PlayerSnapshot> WaitForDecisionAsync(
            TableSession session,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < 50; i++)
            {
                PlayerSnapshot? snapshot = session.PlayerSnapshot;
                if (snapshot?.PrivateView?.Decision != null &&
                    snapshot.PrivateView.Decision.Actions.Count > 0)
                {
                    return snapshot;
                }

                await Task.Delay(20, cancellationToken);
            }

            throw new TimeoutException("Player snapshot did not receive an open decision in time.");
        }

        private static async Task<PlayerSnapshot> WaitForActionTypeAsync(
            TableSession session,
            ProtocolActionType actionType,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < 50; i++)
            {
                PlayerSnapshot? snapshot = session.PlayerSnapshot;
                if (snapshot?.PrivateView?.Decision != null &&
                    snapshot.PrivateView.Decision.Actions.Any(a => a.ActionType == (uint)actionType))
                {
                    return snapshot;
                }

                await Task.Delay(20, cancellationToken);
            }

            var current = session.PlayerSnapshot?.PrivateView?.Decision;
            var actionsStr = current == null ? "null" : string.Join(",", current.Actions.Select(a => ((ProtocolActionType)a.ActionType).ToString() + ":" + a.OptionId));
            var handStr = session.PlayerSnapshot?.PrivateView?.ClosedTiles == null ? "null" : string.Join(",", session.PlayerSnapshot.PrivateView.ClosedTiles);
            throw new TimeoutException($"Player snapshot did not receive action {actionType} in time. Current decision: {actionsStr}, hand: {handStr}, stateVersion: {session.PlayerSnapshot?.StateVersion}");
        }

        private static async Task<(ITableChannel, TableSession)> OpenAsync(
            P41Harness harness,
            CancellationToken cancellationToken)
        {
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, cancellationToken);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "p41-test", null), cancellationToken);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
            await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "p41-test"), cancellationToken);
            for (int i = 0; i < 20 && session.State != TableConnectionState.Ready && session.State != TableConnectionState.Faulted; i++)
            {
                await session.PumpOnceAsync(cancellationToken);
            }

            Assert.True(
                session.State == TableConnectionState.Ready,
                "state=" + session.State + " last=" + session.LastError);
            return (channel, session);
        }

        private static RoundStateMachine BuildRoundWithAssignedSlots(int[] targetSlots, int[] targetEntities)
        {
            var map = new Dictionary<int, int>();
            for (int i = 0; i < targetSlots.Length; i++)
            {
                map[targetSlots[i]] = targetEntities[i];
            }

            return BuildRoundWithSpecificSlots(map);
        }

        private static RoundStateMachine BuildRoundWithSpecificSlots(IReadOnlyDictionary<int, int> specific)
        {
            var used = new HashSet<int>(specific.Values);
            var remaining = new Queue<int>(Enumerable.Range(0, TileId.Count).Where(id => !used.Contains(id)));
            var order = new int[TileId.Count];
            for (int i = 0; i < TileId.Count; i++)
            {
                if (specific.TryGetValue(i, out int entity))
                {
                    order[i] = entity;
                }
                else
                {
                    order[i] = remaining.Dequeue();
                }
            }

            var tiles = order.Select(id => new TileId(id)).ToArray();
            RecordedRandomDraw wall = RecordedRandomDraw.Create("test-wall", tiles);
            TilePositionMap dealt = TilePositionMap.CreateDealt(wall, 0);
            var spec = new RoundSpec(
                "test-round",
                0,
                1,
                0,
                0,
                new long[] { 25_000L, 25_000L, 25_000L, 25_000L },
                0);
            return RoundStateMachine.CreateDealt(spec, dealt, RoundPhase.CheatWindow);
        }

    }
}
