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
            specific[57] = 104;  // Seat 0 turn 2 draw -> the actual drawn entity is 104 (the fixture, not the stale 84 comment); discards 130
            specific[58] = 3;   // Seat 1 turn 2 draw -> tile 3 (4th 1m) -> Seat 1 calls AddedKan! Chankan window opens!

            harness.Options.InitialRound = BuildRoundWithSpecificSlots(specific);

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(25));
                Task pump = StartPump(session, pumpCts.Token);

                // 1. Seat 0 discards 132
                PlayerSnapshot snapshot = await LocalHostCommandTests.WaitForPlayerSnapshotAsync(
                    session,
                    candidate => candidate.PrivateView?.Decision != null &&
                                 candidate.PrivateView.Decision.Actions.Count > 0,
                    "seat0 opening decision",
                    pumpCts.Token);
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

                CommandSubmission discardSubmission = await session.SubmitAsync(discardCommand, pumpCts.Token);
                Assert.True(
                    discardSubmission.Sent,
                    "seat0-discard-132 was not handed to the transport. " +
                    LocalHostCommandTests.DescribeSession(session));
                TableReceiptResolvedEventArgs discardReceipt;
                try
                {
                    discardReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                        discardResolved.Task,
                        pumpCts.Token);
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException(
                        "seat0-discard-132 receipt timed out; decision=" + decision.DecisionId +
                        " stateVersion=" + snapshot.StateVersion +
                        " sent=" + discardSubmission.Sent +
                        ". " + LocalHostCommandTests.DescribeSession(session) +
                        " lastSettlement=" + DescribeLastSettlement(),
                        exception);
                }

                Assert.True(
                    discardReceipt.Resolution.ReceiptStatus == ReceiptStatus.Applied,
                    "seat0-discard-132 rejected: " + discardReceipt.Resolution.ReasonCode +
                    " evaluated=" + discardReceipt.Resolution.EvaluatedStateVersion +
                    ". " + LocalHostCommandTests.DescribeSession(session));
                Assert.True(discardReceipt.Resolution.CommandVersion.HasValue, "discard-132 receipt has no command version.");
                Assert.True(discardReceipt.Resolution.EventSeq.HasValue, "discard-132 receipt has no event seq.");

                // 2. Seat 0 draws the fixture's entity 104 on turn 2 and discards 130.
                // The receipt only proves the discard committed; the next decision is a
                // separate PUSH. Require the post-discard version/event floor, a new
                // decision id, the actual draw (104) and the 130 candidate entity.
                ulong afterFirstDiscardVersion = discardReceipt.Resolution.CommandVersion!.Value.Value;
                ulong afterFirstDiscardEventSeq = discardReceipt.Resolution.EventSeq!.Value.Value;
                PlayerSnapshot turn2Snapshot = await LocalHostCommandTests.WaitForPlayerSnapshotAsync(
                    session,
                    candidate => IsNewDiscardDecision(
                        candidate,
                        snapshot.RoundId,
                        decision.DecisionId,
                        afterFirstDiscardVersion,
                        afterFirstDiscardEventSeq,
                        requiredTileId: 130u,
                        expectedDrawnTileId: 104u),
                    "seat0 turn-2 discard decision after seat0-discard-132",
                    pumpCts.Token);
                DecisionView turn2Decision = turn2Snapshot.PrivateView.Decision!;
                LegalActionView turn2Discard = turn2Decision.Actions.First(
                    action => action.ActionType == (uint)ProtocolActionType.DiscardTile &&
                              action.TileIds.Contains(130u));
                Assert.Equal(turn2Snapshot.RoundId, snapshot.RoundId);
                Assert.NotEqual(decision.DecisionId, turn2Decision.DecisionId);
                Assert.True(turn2Snapshot.StateVersion > afterFirstDiscardVersion);
                Assert.True(turn2Snapshot.EventSeq > afterFirstDiscardEventSeq);
                Assert.True(
                    turn2Snapshot.PrivateView.HasDrawnTileId &&
                    turn2Snapshot.PrivateView.DrawnTileId == 104u,
                    "turn-2 decision did not carry the configured draw 104.");
                Assert.Contains(130u, turn2Discard.TileIds);

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
                    tileIds: turn2Discard.TileIds.ToArray());
                var discard2Command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    discard2Payload,
                    new RequestId("seat0-discard-130"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(turn2Snapshot.StateVersion),
                    new RoundId(turn2Snapshot.RoundId),
                    decisionId: new DecisionId(turn2Decision.DecisionId),
                    controlEpoch: new ControlEpoch(turn2Decision.ControlEpoch));

                CommandSubmission discard2Submission = await session.SubmitAsync(discard2Command, pumpCts.Token);
                Assert.True(
                    discard2Submission.Sent,
                    "seat0-discard-130 was not handed to the transport; decision=" +
                    turn2Decision.DecisionId + " stateVersion=" + turn2Snapshot.StateVersion +
                    ". " + LocalHostCommandTests.DescribeSession(session));
                TableReceiptResolvedEventArgs discard2Receipt;
                try
                {
                    discard2Receipt = await LocalHostCommandTests.WithTimeoutAsync(
                        discard2Resolved.Task,
                        pumpCts.Token);
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException(
                        "seat0-discard-130 receipt timed out; decision=" + turn2Decision.DecisionId +
                        " stateVersion=" + turn2Snapshot.StateVersion +
                        " sent=" + discard2Submission.Sent +
                        ". " + LocalHostCommandTests.DescribeSession(session) +
                        " lastSettlement=" + DescribeLastSettlement(),
                        exception);
                }

                Assert.True(
                    discard2Receipt.Resolution.ReceiptStatus == ReceiptStatus.Applied,
                    "seat0-discard-130 rejected: " + discard2Receipt.Resolution.ReasonCode +
                    " evaluated=" + discard2Receipt.Resolution.EvaluatedStateVersion +
                    ". " + LocalHostCommandTests.DescribeSession(session));
                Assert.True(discard2Receipt.Resolution.CommandVersion.HasValue, "discard-130 receipt has no command version.");
                Assert.True(discard2Receipt.Resolution.EventSeq.HasValue, "discard-130 receipt has no event seq.");
                Assert.Equal(turn2Snapshot.StateVersion + 1, discard2Receipt.Resolution.CommandVersion!.Value.Value);

                // 3. Seat 1 draws tile 3 (1m) and declares AddedKan!
                // Seat 0 receives DeclareRon in ChankanWindow. The chankan window is
                // a new decision after the discard-130 receipt; wait for exactly that
                // decision rather than any snapshot that still carries the old one.
                ulong afterSecondDiscardVersion = discard2Receipt.Resolution.CommandVersion!.Value.Value;
                ulong afterSecondDiscardEventSeq = discard2Receipt.Resolution.EventSeq!.Value.Value;
                PlayerSnapshot ronSnapshot = await LocalHostCommandTests.WaitForPlayerSnapshotAsync(
                    session,
                    candidate => IsNewActionDecision(
                        candidate,
                        turn2Snapshot.RoundId,
                        turn2Decision.DecisionId,
                        afterSecondDiscardVersion,
                        afterSecondDiscardEventSeq,
                        ProtocolActionType.DeclareRon),
                    "seat0 chankan DeclareRon decision after seat0-discard-130",
                    pumpCts.Token);
                Assert.NotNull(ronSnapshot.PrivateView.Decision);
                DecisionView ronDecision = ronSnapshot.PrivateView.Decision!;
                Assert.NotEqual(turn2Decision.DecisionId, ronDecision.DecisionId);

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
                    new StateVersion(ronSnapshot.StateVersion),
                    new RoundId(ronSnapshot.RoundId),
                    decisionId: new DecisionId(ronDecision.DecisionId),
                    controlEpoch: new ControlEpoch(ronDecision.ControlEpoch));

                CommandSubmission ronSub = await session.SubmitAsync(ronCommand, pumpCts.Token);
                Assert.True(
                    ronSub.Sent,
                    "seat0-chankan-ron-1 was not handed to the transport; decision=" +
                    ronDecision.DecisionId + " stateVersion=" + ronSnapshot.StateVersion +
                    ". " + LocalHostCommandTests.DescribeSession(session));

                TableReceiptResolvedEventArgs ronReceipt;
                try
                {
                    ronReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                        ronResolved.Task,
                        pumpCts.Token);
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException(
                        "seat0-chankan-ron-1 receipt timed out; decision=" + ronDecision.DecisionId +
                        " stateVersion=" + ronSnapshot.StateVersion +
                        " sent=" + ronSub.Sent + ". " +
                        LocalHostCommandTests.DescribeSession(session) +
                        " lastSettlement=" + DescribeLastSettlement(),
                        exception);
                }

                Assert.True(
                    ronReceipt.Resolution.ReceiptStatus == ReceiptStatus.Applied,
                    "ron rejected: " + ronReceipt.Resolution.ReasonCode +
                    ". " + LocalHostCommandTests.DescribeSession(session));
                Assert.True(ronReceipt.Resolution.CommandVersion.HasValue, "ron receipt has no command version.");
                Assert.True(ronReceipt.Resolution.EventSeq.HasValue, "ron receipt has no event seq.");

                PlayerSnapshot terminalSnapshot = await LocalHostCommandTests.WaitForPlayerSnapshotAsync(
                    session,
                    candidate => candidate.PublicView.Board.Phase == GamePhase.RoundEnd &&
                                 candidate.StateVersion >= ronReceipt.Resolution.CommandVersion!.Value.Value &&
                                 candidate.EventSeq >= ronReceipt.Resolution.EventSeq!.Value.Value,
                    "public snapshot reaches RoundEnd at the applied ron version",
                    pumpCts.Token);
                Assert.Equal(GamePhase.RoundEnd, terminalSnapshot.PublicView.Board.Phase);

                RoundSettlementCalculation? chankanCalc = ((LocalTableChannel)channel).RuntimeForMatch?.ActorForTest.LastSettlementCalculation;
                Assert.NotNull(chankanCalc);
                Assert.Equal(RoundOutcomeKind.Ron, chankanCalc.Outcome.Kind);
                Assert.Equal(0, chankanCalc.Outcome.WinnerSeat);
                Assert.Equal(1, chankanCalc.Outcome.LoserSeat);
                Assert.NotNull(chankanCalc.Breakdown);
                Assert.Contains(chankanCalc.Breakdown.Yaku, y => y.Yaku.Code == "CHANKAN");
                Assert.Equal(1, chankanCalc.Breakdown.TotalYakuHan);
                Assert.Equal(40, chankanCalc.Breakdown.Fu.FinalFu);
                Assert.Equal(2000L, chankanCalc.Breakdown.TotalPoints);
                Assert.Equal(2000L, chankanCalc.Payments.Deltas[0]);
                Assert.Equal(-2000L, chankanCalc.Payments.Deltas[1]);
                Assert.Equal(0L, chankanCalc.Payments.Deltas[2]);
                Assert.Equal(0L, chankanCalc.Payments.Deltas[3]);

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

        private static bool IsNewDiscardDecision(
            PlayerSnapshot snapshot,
            string roundId,
            string previousDecisionId,
            ulong minStateVersion,
            ulong minEventSeq,
            uint requiredTileId,
            uint expectedDrawnTileId)
        {
            if (!string.Equals(snapshot.RoundId, roundId, StringComparison.Ordinal) ||
                snapshot.StateVersion < minStateVersion ||
                snapshot.EventSeq < minEventSeq)
            {
                return false;
            }

            var privateView = snapshot.PrivateView;
            DecisionView? decision = privateView?.Decision;
            if (privateView == null || decision == null || decision.Actions.Count == 0)
            {
                return false;
            }

            if (string.Equals(decision.DecisionId, previousDecisionId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!privateView.HasDrawnTileId ||
                privateView.DrawnTileId != expectedDrawnTileId)
            {
                return false;
            }

            if (!privateView.ClosedTiles.Contains(requiredTileId))
            {
                return false;
            }

            return decision.Actions.Any(
                action => action.ActionType == (uint)ProtocolActionType.DiscardTile &&
                          action.TileIds.Contains(requiredTileId));
        }

        private static bool IsNewActionDecision(
            PlayerSnapshot snapshot,
            string roundId,
            string previousDecisionId,
            ulong minStateVersion,
            ulong minEventSeq,
            ProtocolActionType actionType)
        {
            if (!string.Equals(snapshot.RoundId, roundId, StringComparison.Ordinal) ||
                snapshot.StateVersion < minStateVersion ||
                snapshot.EventSeq < minEventSeq)
            {
                return false;
            }

            DecisionView? decision = snapshot.PrivateView?.Decision;
            if (decision == null ||
                string.Equals(decision.DecisionId, previousDecisionId, StringComparison.Ordinal))
            {
                return false;
            }

            return decision.Actions.Any(action => action.ActionType == (uint)actionType);
        }

        private static string DescribeLastSettlement()
        {
            TableOperationResult? settlement = LocalTableRuntime.LastSettlementResult;
            return settlement == null
                ? "<none>"
                : settlement.Status + "/" + settlement.ReasonCode + "/" + settlement.Detail +
                  " lastCommandException=" + (LocalTableRuntime.LastCommandException?.GetType().Name ?? "<none>") +
                  ":" + (LocalTableRuntime.LastCommandException?.Message ?? "<none>");
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
