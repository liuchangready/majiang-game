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
    public sealed class LocalHostCandidateAcceptanceTests
    {
        [Fact]
        public async Task KokushiMusouTsumoProbeReachesTerminal()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Probe specification:
            // Entities: [0, 32, 36, 68, 72, 104, 108, 112, 116, 120, 124, 128, 132, 1]
            // Dealer slots: [0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52]
            int[] kokushiEntities = { 0, 32, 36, 68, 72, 104, 108, 112, 116, 120, 124, 128, 132, 1 };
            int[] dealerSlots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            harness.Options.InitialRound = BuildRoundWithAssignedSlots(dealerSlots, kokushiEntities);

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(25));
                Task pump = StartPump(session, pumpCts.Token);

                // Wait for player snapshot with active decision
                PlayerSnapshot snapshot = await WaitForDecisionAsync(session, pumpCts.Token);
                Assert.NotNull(snapshot.PrivateView.Decision);
                DecisionView decision = snapshot.PrivateView.Decision;

                // Kokushi must project DeclareTsumo
                LegalActionView? tsumoAction = decision.Actions.FirstOrDefault(
                    a => a.ActionType == (uint)ProtocolActionType.DeclareTsumo);
                Assert.NotNull(tsumoAction);

                var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) => resolved.TrySetResult(args);

                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    ProtocolActionType.DeclareTsumo);
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    new RequestId("kokushi-tsumo-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snapshot.StateVersion),
                    new RoundId(snapshot.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));

                CommandSubmission submission = await session.SubmitAsync(command, pumpCts.Token);
                Assert.True(submission.Sent);

                TableReceiptResolvedEventArgs receipt = await LocalHostCommandTests.WithTimeoutAsync(
                    resolved.Task,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, receipt.Resolution.ReceiptStatus);

                for (int i = 0; i < 20 &&
                     session.PlayerSnapshot!.PublicView.Board.Phase != GamePhase.RoundEnd; i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }

                Assert.Equal(GamePhase.RoundEnd, session.PlayerSnapshot!.PublicView.Board.Phase);
                Assert.NotNull(session.PlayerSnapshot.PublicView.Session);
                Assert.Equal(4, session.PlayerSnapshot.PublicView.Session.FinalScores.Count);

                RoundSettlementCalculation? kokushiCalc = ((LocalTableChannel)channel).RuntimeForMatch?.ActorForTest.LastSettlementCalculation;
                Assert.NotNull(kokushiCalc);
                Assert.NotNull(kokushiCalc.Breakdown);
                Assert.Contains(kokushiCalc.Breakdown.Yaku, y => y.Yaku.Code == "KOKUSHI");
                Assert.True(kokushiCalc.Breakdown.YakumanCount >= 1);
                Assert.Equal(96000L, kokushiCalc.Breakdown.TotalPoints);
                Assert.Equal(96000L, kokushiCalc.Payments.Deltas[0]);
                Assert.Equal(-32000L, kokushiCalc.Payments.Deltas[1]);
                Assert.Equal(-32000L, kokushiCalc.Payments.Deltas[2]);
                Assert.Equal(-32000L, kokushiCalc.Payments.Deltas[3]);

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task RiichiCandidateAndSubmissionTest()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Hand: 1-9m (0,4,8,12,16,20,24,28,32), 1-1p (36,37), 1-2s (72,76), extra tile 5z (124)
            int[] handTiles = { 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 37, 72, 76, 124 };
            int[] dealerSlots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            harness.Options.InitialRound = BuildRoundWithAssignedSlots(dealerSlots, handTiles);

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(25));
                Task pump = StartPump(session, pumpCts.Token);

                PlayerSnapshot snapshot = await WaitForDecisionAsync(session, pumpCts.Token);
                Assert.NotNull(snapshot.PrivateView.Decision);
                DecisionView decision = snapshot.PrivateView.Decision;

                LegalActionView? riichiAction = decision.Actions.FirstOrDefault(
                    a => a.ActionType == (uint)ProtocolActionType.DeclareRiichi &&
                         a.TileIds.Contains(124u));
                Assert.NotNull(riichiAction);

                var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "riichi-1")
                    {
                        resolved.TrySetResult(args);
                    }
                };

                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    ProtocolActionType.DeclareRiichi,
                    optionId: riichiAction.OptionId,
                    tileIds: new uint[] { 124u });
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    new RequestId("riichi-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snapshot.StateVersion),
                    new RoundId(snapshot.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));

                var snapshotReceived = new TaskCompletionSource<PlayerSnapshot>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.SnapshotChanged += (_, args) =>
                {
                    if (args.PlayerSnapshot != null && args.PlayerSnapshot.StateVersion > snapshot.StateVersion)
                    {
                        snapshotReceived.TrySetResult(args.PlayerSnapshot);
                    }
                };

                CommandSubmission submission = await session.SubmitAsync(command, pumpCts.Token);
                Assert.True(submission.Sent);

                TableReceiptResolvedEventArgs receipt = await LocalHostCommandTests.WithTimeoutAsync(
                    resolved.Task,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, receipt.Resolution.ReceiptStatus);

                PlayerSnapshot riichiSnap = await LocalHostCommandTests.WithTimeoutAsync(
                    snapshotReceived.Task,
                    pumpCts.Token);
                Assert.True(riichiSnap.PublicView.Board.Players[0].Riichi);

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task ClosedKanAndRinshanTsumoTest()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Hand: 4x 1m (0, 1, 2, 3), 1-9p (36, 40, 44, 48, 52, 56, 60, 64, 68), 1s (72).
            // Hand is waiting on 1s.
            // Rinshan slot 122 (DeadWall(0)) is 1s (73).
            int[] handTiles = { 0, 1, 2, 3, 36, 40, 44, 48, 52, 56, 60, 64, 68, 72 };
            int[] dealerSlots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            var specificSlots = new Dictionary<int, int>();
            for (int i = 0; i < dealerSlots.Length; i++)
            {
                specificSlots[dealerSlots[i]] = handTiles[i];
            }

            specificSlots[122] = 73; // Rinshan draw

            harness.Options.InitialRound = BuildRoundWithSpecificSlots(specificSlots);

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(25));
                Task pump = StartPump(session, pumpCts.Token);

                PlayerSnapshot snapshot = await WaitForDecisionAsync(session, pumpCts.Token);
                Assert.NotNull(snapshot.PrivateView.Decision);
                DecisionView decision = snapshot.PrivateView.Decision;

                LegalActionView? kanAction = decision.Actions.FirstOrDefault(
                    a => a.ActionType == (uint)ProtocolActionType.DeclareKan &&
                         a.OptionId.StartsWith("closed-kan-", StringComparison.Ordinal));
                Assert.NotNull(kanAction);
                Assert.Equal(4, kanAction.TileIds.Count);
                Assert.Contains(0u, kanAction.TileIds);
                Assert.Contains(1u, kanAction.TileIds);
                Assert.Contains(2u, kanAction.TileIds);
                Assert.Contains(3u, kanAction.TileIds);

                // 1. Submit ClosedKan
                var kanResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "closed-kan-1")
                    {
                        kanResolved.TrySetResult(args);
                    }
                };

                BusinessPayload kanPayload = CommandPayloadFactory.SubmitAction(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    ProtocolActionType.DeclareKan,
                    optionId: kanAction.OptionId,
                    tileIds: kanAction.TileIds.ToArray(),
                    meldType: 4u);
                var kanCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    kanPayload,
                    new RequestId("closed-kan-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snapshot.StateVersion),
                    new RoundId(snapshot.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));

                var snapshot1Received = new TaskCompletionSource<PlayerSnapshot>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.SnapshotChanged += (_, args) =>
                {
                    if (args.PlayerSnapshot != null && args.PlayerSnapshot.StateVersion > snapshot.StateVersion)
                    {
                        snapshot1Received.TrySetResult(args.PlayerSnapshot);
                    }
                };

                CommandSubmission kanSub = await session.SubmitAsync(kanCommand, pumpCts.Token);
                Assert.True(kanSub.Sent);

                TableReceiptResolvedEventArgs kanReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                    kanResolved.Task,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, kanReceipt.Resolution.ReceiptStatus);

                PlayerSnapshot tsumoSnapshot = await WaitForActionTypeAsync(
                    session,
                    ProtocolActionType.DeclareTsumo,
                    pumpCts.Token);
                DecisionView tsumoDecision = tsumoSnapshot.PrivateView.Decision;

                var tsumoResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "rinshan-tsumo-1")
                    {
                        tsumoResolved.TrySetResult(args);
                    }
                };

                BusinessPayload tsumoPayload = CommandPayloadFactory.SubmitAction(
                    tsumoSnapshot.RoundId,
                    tsumoDecision.DecisionId,
                    tsumoDecision.ControlEpoch,
                    ProtocolActionType.DeclareTsumo);
                var tsumoCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    tsumoPayload,
                    new RequestId("rinshan-tsumo-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(tsumoSnapshot.StateVersion),
                    new RoundId(tsumoSnapshot.RoundId),
                    decisionId: new DecisionId(tsumoDecision.DecisionId),
                    controlEpoch: new ControlEpoch(tsumoDecision.ControlEpoch));

                CommandSubmission tsumoSub = await session.SubmitAsync(tsumoCommand, pumpCts.Token);
                Assert.True(tsumoSub.Sent);

                TableReceiptResolvedEventArgs tsumoReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                    tsumoResolved.Task,
                    pumpCts.Token);
                Assert.True(tsumoReceipt.Resolution.ReceiptStatus == ReceiptStatus.Applied, $"tsumoReceipt rejected: {tsumoReceipt.Resolution.ReasonCode}, evaluated={tsumoReceipt.Resolution.EvaluatedStateVersion}");

                for (int i = 0; i < 20 &&
                     session.PlayerSnapshot!.PublicView.Board.Phase != GamePhase.RoundEnd; i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }

                Assert.Equal(GamePhase.RoundEnd, session.PlayerSnapshot!.PublicView.Board.Phase);

                RoundSettlementCalculation? kanCalc = ((LocalTableChannel)channel).RuntimeForMatch?.ActorForTest.LastSettlementCalculation;
                Assert.NotNull(kanCalc);
                Assert.Equal(RoundOutcomeKind.Tsumo, kanCalc.Outcome.Kind);
                Assert.Equal(0, kanCalc.Outcome.WinnerSeat);
                Assert.NotNull(kanCalc.Breakdown);
                Assert.Equal(4, kanCalc.Breakdown.TotalYakuHan);
                Assert.Equal(60, kanCalc.Breakdown.Fu.FinalFu);
                Assert.Equal(12000L, kanCalc.Breakdown.TotalPoints);
                Assert.Contains(kanCalc.Breakdown.Yaku, y => y.Yaku.Code == "RINSHAN");
                Assert.Contains(kanCalc.Breakdown.Yaku, y => y.Yaku.Code == "MENZEN_TSUMO");
                Assert.Contains(kanCalc.Breakdown.Yaku, y => y.Yaku.Code == "ITTSU");
                Assert.Equal(12000L, kanCalc.Payments.Deltas[0]);
                Assert.Equal(-4000L, kanCalc.Payments.Deltas[1]);
                Assert.Equal(-4000L, kanCalc.Payments.Deltas[2]);
                Assert.Equal(-4000L, kanCalc.Payments.Deltas[3]);

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task PonCandidateAndSubmissionTest()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Seat 0: Two 1m (0, 1), and arbitrary other tiles
            // Seat 0 will discard tile 132 (Chun).
            // Seat 1 is next: AI draws tile 2 (1m) and discards it!
            // When Seat 1 discards 1m, Seat 0 has [0, 1], so Pon candidate opens!
            var specific = new Dictionary<int, int>();
            int[] seat0Tiles = { 0, 1, 4, 8, 12, 16, 20, 24, 28, 36, 40, 72, 76, 132 };
            int[] seat0Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            for (int i = 0; i < seat0Slots.Length; i++)
            {
                specific[seat0Slots[i]] = seat0Tiles[i];
            }

            // Wall slot 53 is Seat 1's draw tile: 1m (2)
            specific[53] = 2;

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
                    if (args.Command.Key.RequestId.Value == "seat0-discard-1")
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
                    new RequestId("seat0-discard-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snapshot.StateVersion),
                    new RoundId(snapshot.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));

                await session.SubmitAsync(discardCommand, pumpCts.Token);
                await LocalHostCommandTests.WithTimeoutAsync(discardResolved.Task, pumpCts.Token);

                // 2. Seat 1 draws 2 (1m) and discards it. Seat 0 receives ResponseWindow with DeclarePon!
                PlayerSnapshot ponSnapshot = await WaitForActionTypeAsync(
                    session,
                    ProtocolActionType.DeclarePon,
                    pumpCts.Token);
                Assert.NotNull(ponSnapshot.PrivateView.Decision);
                DecisionView ponDecision = ponSnapshot.PrivateView.Decision;

                var ponResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-pon-1")
                    {
                        ponResolved.TrySetResult(args);
                    }
                };

                LegalActionView? ponAction = ponDecision.Actions.FirstOrDefault(
                    a => a.ActionType == (uint)ProtocolActionType.DeclarePon);
                Assert.NotNull(ponAction);
                Assert.Equal(3, ponAction.TileIds.Count);
                Assert.Contains(2u, ponAction.TileIds);
                Assert.Contains(0u, ponAction.TileIds);
                Assert.Contains(1u, ponAction.TileIds);

                BusinessPayload ponPayload = CommandPayloadFactory.SubmitAction(
                    ponSnapshot.RoundId,
                    ponDecision.DecisionId,
                    ponDecision.ControlEpoch,
                    ProtocolActionType.DeclarePon,
                    tileIds: ponAction.TileIds.ToArray());
                var ponCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    ponPayload,
                    new RequestId("seat0-pon-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(ponSnapshot.StateVersion),
                    new RoundId(ponSnapshot.RoundId),
                    decisionId: new DecisionId(ponDecision.DecisionId),
                    controlEpoch: new ControlEpoch(ponDecision.ControlEpoch));

                CommandSubmission ponSub = await session.SubmitAsync(ponCommand, pumpCts.Token);
                Assert.True(ponSub.Sent);

                TableReceiptResolvedEventArgs ponReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                    ponResolved.Task,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, ponReceipt.Resolution.ReceiptStatus);

                // Assert Pon meld formed for seat 0
                for (int i = 0; i < 40 && session.PlayerSnapshot!.PublicView.Board.Players[0].Melds.Count == 0; i++)
                {
                    await Task.Delay(50, pumpCts.Token);
                }
                Assert.Single(session.PlayerSnapshot!.PublicView.Board.Players[0].Melds);
                var meld = session.PlayerSnapshot.PublicView.Board.Players[0].Melds[0];
                Assert.Equal(MeldType.Pon, meld.Type);
                Assert.Equal(2u, meld.CalledTileId);
                Assert.Equal(1, meld.FromSeat);
                Assert.Equal(3, meld.TileIds.Count);

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task ChiCandidateAndSubmissionTest()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Seat 0: 1m (0), 2m (4), and discard tile 132 (Chun).
            // Seat 0 discards 132.
            // Seat 1 draws 133 and discards 133.
            // Seat 2 draws 134 and discards 134.
            // Seat 3 (kamicha of Seat 0) draws 3m (8) and discards 3m (8)!
            // When Seat 3 discards 3m, Seat 0 can Chi (1m, 2m, 3m)!
            var specific = new Dictionary<int, int>();
            int[] seat0Tiles = { 0, 4, 12, 16, 20, 24, 28, 36, 40, 44, 48, 52, 56, 132 };
            int[] seat0Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            for (int i = 0; i < seat0Slots.Length; i++)
            {
                specific[seat0Slots[i]] = seat0Tiles[i];
            }

            specific[53] = 133; // Seat 1 draw
            specific[54] = 134; // Seat 2 draw
            specific[55] = 8;   // Seat 3 draw: 3m (8)
            specific[100] = 9;  // Put other 3m tiles late in the wall so AI seats cannot Pon 3m
            specific[101] = 10;
            specific[102] = 11;

            harness.Options.InitialRound = BuildRoundWithSpecificSlots(specific);

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(25));
                Task pump = StartPump(session, pumpCts.Token);

                // Seat 0 discards 132
                PlayerSnapshot snapshot = await WaitForDecisionAsync(session, pumpCts.Token);
                DecisionView decision = snapshot.PrivateView.Decision!;

                var discardResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-discard-chi-1")
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
                    new RequestId("seat0-discard-chi-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snapshot.StateVersion),
                    new RoundId(snapshot.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));

                await session.SubmitAsync(discardCommand, pumpCts.Token);
                await LocalHostCommandTests.WithTimeoutAsync(discardResolved.Task, pumpCts.Token);

                // After seats 1, 2, 3 discard, Seat 3 discards 8 (3m). Seat 0 gets DeclareChi!
                PlayerSnapshot chiSnapshot = await WaitForActionTypeAsync(
                    session,
                    ProtocolActionType.DeclareChi,
                    pumpCts.Token);
                Assert.NotNull(chiSnapshot.PrivateView.Decision);
                DecisionView chiDecision = chiSnapshot.PrivateView.Decision;

                var chiResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-chi-1")
                    {
                        chiResolved.TrySetResult(args);
                    }
                };

                LegalActionView? chiAction = chiDecision.Actions.FirstOrDefault(
                    a => a.ActionType == (uint)ProtocolActionType.DeclareChi);
                Assert.NotNull(chiAction);
                Assert.Equal(3, chiAction.TileIds.Count);
                Assert.Contains(8u, chiAction.TileIds);
                Assert.Contains(0u, chiAction.TileIds);
                Assert.Contains(4u, chiAction.TileIds);

                BusinessPayload chiPayload = CommandPayloadFactory.SubmitAction(
                    chiSnapshot.RoundId,
                    chiDecision.DecisionId,
                    chiDecision.ControlEpoch,
                    ProtocolActionType.DeclareChi,
                    tileIds: chiAction.TileIds.ToArray());
                var chiCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    chiPayload,
                    new RequestId("seat0-chi-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(chiSnapshot.StateVersion),
                    new RoundId(chiSnapshot.RoundId),
                    decisionId: new DecisionId(chiDecision.DecisionId),
                    controlEpoch: new ControlEpoch(chiDecision.ControlEpoch));

                CommandSubmission chiSub = await session.SubmitAsync(chiCommand, pumpCts.Token);
                Assert.True(chiSub.Sent);

                TableReceiptResolvedEventArgs chiReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                    chiResolved.Task,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, chiReceipt.Resolution.ReceiptStatus);

                // Assert Chi meld formed for seat 0
                for (int i = 0; i < 40 && session.PlayerSnapshot!.PublicView.Board.Players[0].Melds.Count == 0; i++)
                {
                    await Task.Delay(50, pumpCts.Token);
                }
                Assert.Single(session.PlayerSnapshot!.PublicView.Board.Players[0].Melds);
                var chiMeld = session.PlayerSnapshot.PublicView.Board.Players[0].Melds[0];
                Assert.Equal(MeldType.Chi, chiMeld.Type);
                Assert.Equal(8u, chiMeld.CalledTileId);
                Assert.Equal(3, chiMeld.FromSeat);
                Assert.Equal(3, chiMeld.TileIds.Count);

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task OpenKanCandidateAndSubmissionTest()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Seat 0: Three 1m (0, 1, 2) and tile 132.
            // Seat 0 discards 132.
            // Seat 1 draws 4th 1m (3) and discards it!
            // Seat 0 gets DeclareKan (OpenKan)!
            var specific = new Dictionary<int, int>();
            int[] seat0Tiles = { 0, 1, 2, 8, 12, 16, 20, 24, 28, 36, 40, 72, 76, 132 };
            int[] seat0Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            for (int i = 0; i < seat0Slots.Length; i++)
            {
                specific[seat0Slots[i]] = seat0Tiles[i];
            }

            specific[53] = 3; // Seat 1 draws 4th 1m (3)

            harness.Options.InitialRound = BuildRoundWithSpecificSlots(specific);

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(25));
                Task pump = StartPump(session, pumpCts.Token);

                // Seat 0 discards 132
                PlayerSnapshot snapshot = await WaitForDecisionAsync(session, pumpCts.Token);
                DecisionView decision = snapshot.PrivateView.Decision!;

                var discardResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-discard-openkan-1")
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
                    new RequestId("seat0-discard-openkan-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snapshot.StateVersion),
                    new RoundId(snapshot.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));

                await session.SubmitAsync(discardCommand, pumpCts.Token);
                await LocalHostCommandTests.WithTimeoutAsync(discardResolved.Task, pumpCts.Token);

                // Seat 0 gets DeclareKan in ResponseWindow
                PlayerSnapshot kanSnapshot = await WaitForActionTypeAsync(
                    session,
                    ProtocolActionType.DeclareKan,
                    pumpCts.Token);
                Assert.NotNull(kanSnapshot.PrivateView.Decision);
                DecisionView kanDecision = kanSnapshot.PrivateView.Decision;

                var kanResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-openkan-1")
                    {
                        kanResolved.TrySetResult(args);
                    }
                };

                LegalActionView? kanAction = kanDecision.Actions.FirstOrDefault(
                    a => a.ActionType == (uint)ProtocolActionType.DeclareKan);
                Assert.NotNull(kanAction);
                Assert.Equal(4, kanAction.TileIds.Count);
                Assert.Contains(3u, kanAction.TileIds);
                Assert.Contains(0u, kanAction.TileIds);
                Assert.Contains(1u, kanAction.TileIds);
                Assert.Contains(2u, kanAction.TileIds);

                BusinessPayload kanPayload = CommandPayloadFactory.SubmitAction(
                    kanSnapshot.RoundId,
                    kanDecision.DecisionId,
                    kanDecision.ControlEpoch,
                    ProtocolActionType.DeclareKan,
                    tileIds: kanAction.TileIds.ToArray(),
                    meldType: 3u); // OpenKan = 3
                var kanCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    kanPayload,
                    new RequestId("seat0-openkan-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(kanSnapshot.StateVersion),
                    new RoundId(kanSnapshot.RoundId),
                    decisionId: new DecisionId(kanDecision.DecisionId),
                    controlEpoch: new ControlEpoch(kanDecision.ControlEpoch));

                CommandSubmission kanSub = await session.SubmitAsync(kanCommand, pumpCts.Token);
                Assert.True(kanSub.Sent);

                TableReceiptResolvedEventArgs kanReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                    kanResolved.Task,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, kanReceipt.Resolution.ReceiptStatus);

                // Assert OpenKan meld formed for seat 0
                for (int i = 0; i < 40 && session.PlayerSnapshot!.PublicView.Board.Players[0].Melds.Count == 0; i++)
                {
                    await Task.Delay(50, pumpCts.Token);
                }
                Assert.Single(session.PlayerSnapshot!.PublicView.Board.Players[0].Melds);
                var kanMeld = session.PlayerSnapshot.PublicView.Board.Players[0].Melds[0];
                Assert.Equal(MeldType.OpenKan, kanMeld.Type);
                Assert.Equal(3u, kanMeld.CalledTileId);
                Assert.Equal(1, kanMeld.FromSeat);
                Assert.Equal(4, kanMeld.TileIds.Count);

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task RonCandidateAndSubmissionTest()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Seat 0: 1-9m (0,4,8,12,16,20,24,28,32), 1-2p (36,40), 1-1s (72,73), discard tile 132.
            // Tenpai waiting on 3p (44).
            // Seat 0 discards 132.
            // Seat 1 draws 44 (3p) and discards it!
            // Seat 0 gets DeclareRon in ResponseWindow!
            var specific = new Dictionary<int, int>();
            int[] seat0Tiles = { 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 72, 73, 132 };
            int[] seat0Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            for (int i = 0; i < seat0Slots.Length; i++)
            {
                specific[seat0Slots[i]] = seat0Tiles[i];
            }

            specific[53] = 44; // Seat 1 draws 3p (44)

            harness.Options.InitialRound = BuildRoundWithSpecificSlots(specific);

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(25));
                Task pump = StartPump(session, pumpCts.Token);

                // Seat 0 discards 132
                PlayerSnapshot snapshot = await WaitForDecisionAsync(session, pumpCts.Token);
                DecisionView decision = snapshot.PrivateView.Decision!;

                var discardResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) => discardResolved.TrySetResult(args);

                BusinessPayload discardPayload = CommandPayloadFactory.SubmitAction(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    ProtocolActionType.DiscardTile,
                    tileIds: new uint[] { 132u });
                var discardCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    discardPayload,
                    new RequestId("seat0-discard-ron-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snapshot.StateVersion),
                    new RoundId(snapshot.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));

                await session.SubmitAsync(discardCommand, pumpCts.Token);
                await LocalHostCommandTests.WithTimeoutAsync(discardResolved.Task, pumpCts.Token);

                // Seat 0 gets DeclareRon in ResponseWindow!
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
                session.ReceiptResolved += (_, args) => ronResolved.TrySetResult(args);

                BusinessPayload ronPayload = CommandPayloadFactory.SubmitAction(
                    ronSnapshot.RoundId,
                    ronDecision.DecisionId,
                    ronDecision.ControlEpoch,
                    ProtocolActionType.DeclareRon);
                var ronCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    ronPayload,
                    new RequestId("seat0-ron-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(session.PlayerSnapshot!.StateVersion),
                    new RoundId(ronSnapshot.RoundId),
                    decisionId: new DecisionId(ronDecision.DecisionId),
                    controlEpoch: new ControlEpoch(ronDecision.ControlEpoch));

                CommandSubmission ronSub = await session.SubmitAsync(ronCommand, pumpCts.Token);
                Assert.True(ronSub.Sent);

                TableReceiptResolvedEventArgs ronReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                    ronResolved.Task,
                    pumpCts.Token);
                Assert.True(
                    ronReceipt.Resolution.ReceiptStatus == ReceiptStatus.Applied,
                    "ron rejected: " + ronReceipt.Resolution.ReasonCode);

                for (int i = 0; i < 20 &&
                     session.PlayerSnapshot!.PublicView.Board.Phase != GamePhase.RoundEnd; i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }

                Assert.Equal(GamePhase.RoundEnd, session.PlayerSnapshot!.PublicView.Board.Phase);

                RoundSettlementCalculation? ronCalc = ((LocalTableChannel)channel).RuntimeForMatch?.ActorForTest.LastSettlementCalculation;
                Assert.NotNull(ronCalc);
                Assert.Equal(RoundOutcomeKind.Ron, ronCalc.Outcome.Kind);
                Assert.Equal(0, ronCalc.Outcome.WinnerSeat);
                Assert.Equal(1, ronCalc.Outcome.LoserSeat);
                Assert.NotNull(ronCalc.Breakdown);
                Assert.Equal(2, ronCalc.Breakdown.TotalYakuHan);
                Assert.Equal(40, ronCalc.Breakdown.Fu.FinalFu);
                Assert.Equal(3900L, ronCalc.Breakdown.TotalPoints);
                Assert.Contains(ronCalc.Breakdown.Yaku, y => y.Yaku.Code == "ITTSU");
                Assert.Equal(3900L, ronCalc.Payments.Deltas[0]);
                Assert.Equal(-3900L, ronCalc.Payments.Deltas[1]);
                Assert.Equal(0L, ronCalc.Payments.Deltas[2]);
                Assert.Equal(0L, ronCalc.Payments.Deltas[3]);

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Same legal wall as RonCandidateAndSubmissionTest: seat 0 waits on 3p (44) and
            // seat 1 draws/discards the winning tile. Here seat 0 passes instead of Ron.
            var specific = new Dictionary<int, int>();
            int[] seat0Tiles = { 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 72, 73, 132 };
            int[] seat0Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            for (int i = 0; i < seat0Slots.Length; i++)
            {
                specific[seat0Slots[i]] = seat0Tiles[i];
            }

            specific[53] = 44;
            harness.Options.InitialRound = BuildRoundWithSpecificSlots(specific);

            ulong versionAfterPass;
            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(30));
                Task pump = StartPump(session, pumpCts.Token);

                PlayerSnapshot snapshot = await WaitForDecisionAsync(session, pumpCts.Token);
                DecisionView decision = snapshot.PrivateView.Decision!;
                var discardResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "furiten-seat0-discard")
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
                await session.SubmitAsync(
                    new ProtocolCommand(
                        CommandKind.SubmitAction,
                        discardPayload,
                        new RequestId("furiten-seat0-discard"),
                        session.Binding!.Scope.ScopeId,
                        new StateVersion(snapshot.StateVersion),
                        new RoundId(snapshot.RoundId),
                        decisionId: new DecisionId(decision.DecisionId),
                        controlEpoch: new ControlEpoch(decision.ControlEpoch)),
                    pumpCts.Token);
                TableReceiptResolvedEventArgs discardReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                    discardResolved.Task,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, discardReceipt.Resolution.ReceiptStatus);

                PlayerSnapshot ronSnapshot = await WaitForActionTypeAsync(
                    session,
                    ProtocolActionType.DeclareRon,
                    pumpCts.Token);
                await Task.Delay(150, pumpCts.Token);
                ronSnapshot = session.PlayerSnapshot!;
                DecisionView ronDecision = ronSnapshot.PrivateView.Decision!;
                Assert.NotNull(ronDecision);
                Assert.Contains(
                    ronDecision.Actions,
                    action => action.ActionType == (uint)ProtocolActionType.DeclareRon);

                var passResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "furiten-seat0-pass")
                    {
                        passResolved.TrySetResult(args);
                    }
                };
                BusinessPayload passPayload = CommandPayloadFactory.SubmitAction(
                    ronSnapshot.RoundId,
                    ronDecision.DecisionId,
                    ronDecision.ControlEpoch,
                    ProtocolActionType.Pass);
                await session.SubmitAsync(
                    new ProtocolCommand(
                        CommandKind.SubmitAction,
                        passPayload,
                        new RequestId("furiten-seat0-pass"),
                        session.Binding!.Scope.ScopeId,
                        new StateVersion(ronSnapshot.StateVersion),
                        new RoundId(ronSnapshot.RoundId),
                        decisionId: new DecisionId(ronDecision.DecisionId),
                        controlEpoch: new ControlEpoch(ronDecision.ControlEpoch)),
                    pumpCts.Token);
                TableReceiptResolvedEventArgs passReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                    passResolved.Task,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, passReceipt.Resolution.ReceiptStatus);

                for (int i = 0; i < 50 && session.PlayerSnapshot?.PrivateView?.Furiten != true; i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }

                PlayerSnapshot afterPass = session.PlayerSnapshot!;
                Assert.True(afterPass.PrivateView.Furiten, "the owner projection must carry temporary furiten");
                versionAfterPass = afterPass.StateVersion;
                LocalTableRuntime? runtime = ((LocalTableChannel)channel).RuntimeForMatch;
                Assert.NotNull(runtime);
                Assert.True(runtime!.ActorForTest.FuritenForHost.IsTemporary(0));
                Assert.False(runtime.ActorForTest.FuritenForHost.IsTemporary(1));

                // The pass closed the only response window. A forged Ron on the current state is
                // never applied, and it cannot end the round through a furiten win.
                var ronResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "furiten-ron-after-pass")
                    {
                        ronResolved.TrySetResult(args);
                    }
                };
                BusinessPayload forgedRon = CommandPayloadFactory.SubmitAction(
                    afterPass.RoundId,
                    "local-direct",
                    0UL,
                    ProtocolActionType.DeclareRon);
                await session.SubmitAsync(
                    new ProtocolCommand(
                        CommandKind.SubmitAction,
                        forgedRon,
                        new RequestId("furiten-ron-after-pass"),
                        session.Binding!.Scope.ScopeId,
                        new StateVersion(afterPass.StateVersion),
                        new RoundId(afterPass.RoundId),
                        decisionId: null,
                        controlEpoch: new ControlEpoch(0)),
                    pumpCts.Token);
                TableReceiptResolvedEventArgs rejectedRon = await LocalHostCommandTests.WithTimeoutAsync(
                    ronResolved.Task,
                    pumpCts.Token);
                Assert.NotEqual(ReceiptStatus.Applied, rejectedRon.Resolution.ReceiptStatus);
                Assert.NotEqual(GamePhase.RoundEnd, session.PlayerSnapshot!.PublicView.Board.Phase);

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }

            // The same committed write that returned the receipt also persisted the bits and the
            // latest projection facts; inspect them before any recovery path can normalize them.
            using (LocalTableStore store = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                Assert.True(store.LoadTemporaryFuriten()[0]);
                Assert.False(store.LoadRiichiFuriten()[0]);
                TableCommittedFactsSnapshot facts = Assert.IsType<TableCommittedFactsSnapshot>(
                    await store.LoadCommittedFactsAsync(P41Harness.RoomId, ct));
                TableProjectionFactsCodec.TableProjectionFactsIdentity identity =
                    TableProjectionFactsCodec.ReadIdentity(facts.Payload);
                TableProjectionContextData decoded = TableProjectionFactsCodec.Decode(
                    facts.Payload,
                    identity.RoundId,
                    identity.StateVersion);
                Assert.True(decoded.PrivateFacts[0].TemporaryFuriten);
                Assert.False(decoded.PrivateFacts[1].TemporaryFuriten);
            }

            // Cold restart: the furiten bit must come from the committed projection facts, not
            // from process memory, and must survive until seat 0 actually draws.
            (ITableChannel reopened, TableSession restored) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(30));
                Task pump = StartPump(restored, pumpCts.Token);
                for (int i = 0; i < 50 && restored.PlayerSnapshot?.PrivateView?.Furiten != true; i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }

                Assert.True(restored.PlayerSnapshot!.PrivateView.Furiten, "cold restart lost the committed furiten fact");
                Assert.True(restored.PlayerSnapshot.StateVersion >= versionAfterPass);

                PlayerSnapshot nextTurn = await WaitForDecisionAsync(restored, pumpCts.Token);
                Assert.True(nextTurn.StateVersion > versionAfterPass);
                Assert.False(nextTurn.PrivateView.Furiten, "the next draw must clear temporary furiten");

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await restored.CloseAsync("TEST_DONE", CancellationToken.None);
                await reopened.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task AddedKanAndRinshanTsumoTest()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            int[] handTiles = { 0, 1, 4, 5, 6, 8, 9, 10, 12, 13, 14, 16, 17, 132 };
            int[] dealerSlots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            var specific = new Dictionary<int, int>();
            for (int i = 0; i < dealerSlots.Length; i++)
            {
                specific[dealerSlots[i]] = handTiles[i];
            }

            specific[53] = 2;   // Seat 1 draw: tile 2 (1m) -> discards 2
            specific[54] = 133; // Seat 1 round 2 draw
            specific[55] = 134; // Seat 2 round 2 draw
            specific[56] = 135; // Seat 3 round 2 draw
            specific[57] = 3;   // Seat 0 round 2 draw: tile 3 (1m, the 4th 1m for AddedKan)
            specific[100] = 19; // 4th 5m late in the wall so AI cannot Pon 17
            specific[122] = 18; // Rinshan draw: tile 18 (5m, winning tile!)

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

                // 2. Seat 1 draws 2 (1m) and discards it. Seat 0 receives DeclarePon!
                PlayerSnapshot ponSnapshot = await WaitForActionTypeAsync(
                    session,
                    ProtocolActionType.DeclarePon,
                    pumpCts.Token);
                Assert.NotNull(ponSnapshot.PrivateView.Decision);
                DecisionView ponDecision = ponSnapshot.PrivateView.Decision;

                var ponResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-pon-1")
                    {
                        ponResolved.TrySetResult(args);
                    }
                };

                LegalActionView? ponAction = ponDecision.Actions.FirstOrDefault(
                    a => a.ActionType == (uint)ProtocolActionType.DeclarePon);
                Assert.NotNull(ponAction);
                Assert.Equal(3, ponAction.TileIds.Count);
                Assert.Contains(2u, ponAction.TileIds);
                Assert.Contains(0u, ponAction.TileIds);
                Assert.Contains(1u, ponAction.TileIds);

                BusinessPayload ponPayload = CommandPayloadFactory.SubmitAction(
                    ponSnapshot.RoundId,
                    ponDecision.DecisionId,
                    ponDecision.ControlEpoch,
                    ProtocolActionType.DeclarePon,
                    tileIds: ponAction.TileIds.ToArray());
                var ponCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    ponPayload,
                    new RequestId("seat0-pon-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(session.PlayerSnapshot!.StateVersion),
                    new RoundId(ponSnapshot.RoundId),
                    decisionId: new DecisionId(ponDecision.DecisionId),
                    controlEpoch: new ControlEpoch(ponDecision.ControlEpoch));

                await session.SubmitAsync(ponCommand, pumpCts.Token);
                await LocalHostCommandTests.WithTimeoutAsync(ponResolved.Task, pumpCts.Token);

                // 3. Seat 0 must discard a tile after calling Pon. Discard 17 (5m).
                PlayerSnapshot afterPonSnapshot = await WaitForActionTypeAsync(
                    session,
                    ProtocolActionType.DiscardTile,
                    pumpCts.Token);
                DecisionView afterPonDecision = afterPonSnapshot.PrivateView.Decision!;

                var afterPonResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-discard-after-pon")
                    {
                        afterPonResolved.TrySetResult(args);
                    }
                };

                BusinessPayload afterPonPayload = CommandPayloadFactory.SubmitAction(
                    afterPonSnapshot.RoundId,
                    afterPonDecision.DecisionId,
                    afterPonDecision.ControlEpoch,
                    ProtocolActionType.DiscardTile,
                    tileIds: new uint[] { 17u });
                var afterPonCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    afterPonPayload,
                    new RequestId("seat0-discard-after-pon"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(session.PlayerSnapshot!.StateVersion),
                    new RoundId(afterPonSnapshot.RoundId),
                    decisionId: new DecisionId(afterPonDecision.DecisionId),
                    controlEpoch: new ControlEpoch(afterPonDecision.ControlEpoch));

                await session.SubmitAsync(afterPonCommand, pumpCts.Token);
                await LocalHostCommandTests.WithTimeoutAsync(afterPonResolved.Task, pumpCts.Token);

                // 4. Seats 1, 2, 3 discard 133, 134, 135.
                // Seat 0 draws tile 3 (1m). Seat 0 receives DeclareKan (AddedKan candidate)!
                PlayerSnapshot kanSnapshot = await WaitForActionTypeAsync(
                    session,
                    ProtocolActionType.DeclareKan,
                    pumpCts.Token);
                Assert.NotNull(kanSnapshot.PrivateView.Decision);
                DecisionView kanDecision = kanSnapshot.PrivateView.Decision;
                LegalActionView? addedKanAction = kanDecision.Actions.FirstOrDefault(
                    a => a.ActionType == (uint)ProtocolActionType.DeclareKan &&
                         a.OptionId.StartsWith("added-kan", StringComparison.Ordinal));
                Assert.NotNull(addedKanAction);
                Assert.Equal(4, addedKanAction.TileIds.Count);
                Assert.Contains(3u, addedKanAction.TileIds);

                var kanResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-added-kan-1")
                    {
                        kanResolved.TrySetResult(args);
                    }
                };

                BusinessPayload kanPayload = CommandPayloadFactory.SubmitAction(
                    kanSnapshot.RoundId,
                    kanDecision.DecisionId,
                    kanDecision.ControlEpoch,
                    ProtocolActionType.DeclareKan,
                    optionId: addedKanAction.OptionId,
                    tileIds: addedKanAction.TileIds.ToArray(),
                    meldType: 5u);
                var kanCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    kanPayload,
                    new RequestId("seat0-added-kan-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(session.PlayerSnapshot!.StateVersion),
                    new RoundId(kanSnapshot.RoundId),
                    decisionId: new DecisionId(kanDecision.DecisionId),
                    controlEpoch: new ControlEpoch(kanDecision.ControlEpoch));

                await session.SubmitAsync(kanCommand, pumpCts.Token);
                TableReceiptResolvedEventArgs kanReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                    kanResolved.Task,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, kanReceipt.Resolution.ReceiptStatus);

                // 5. Rinshan drawn (tile 18, 5m), completing the hand!
                // Seat 0 receives DeclareTsumo!
                PlayerSnapshot tsumoSnapshot = await WaitForActionTypeAsync(
                    session,
                    ProtocolActionType.DeclareTsumo,
                    pumpCts.Token);
                Assert.NotNull(tsumoSnapshot.PrivateView.Decision);
                DecisionView tsumoDecision = tsumoSnapshot.PrivateView.Decision;

                var tsumoResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-rinshan-tsumo")
                    {
                        tsumoResolved.TrySetResult(args);
                    }
                };

                BusinessPayload tsumoPayload = CommandPayloadFactory.SubmitAction(
                    tsumoSnapshot.RoundId,
                    tsumoDecision.DecisionId,
                    tsumoDecision.ControlEpoch,
                    ProtocolActionType.DeclareTsumo);
                var tsumoCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    tsumoPayload,
                    new RequestId("seat0-rinshan-tsumo"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(session.PlayerSnapshot!.StateVersion),
                    new RoundId(tsumoSnapshot.RoundId),
                    decisionId: new DecisionId(tsumoDecision.DecisionId),
                    controlEpoch: new ControlEpoch(tsumoDecision.ControlEpoch));

                await session.SubmitAsync(tsumoCommand, pumpCts.Token);
                TableReceiptResolvedEventArgs tsumoReceipt;
                try
                {
                    tsumoReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                        tsumoResolved.Task,
                        pumpCts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new Exception($"tsumoResolved timed out! LastSettlementResult={LocalTableRuntime.LastSettlementResult?.Status}: {LocalTableRuntime.LastSettlementResult?.ReasonCode} {LocalTableRuntime.LastSettlementResult?.Detail}, LastCommandException={LocalTableRuntime.LastCommandException}");
                }
                Assert.Equal(ReceiptStatus.Applied, tsumoReceipt.Resolution.ReceiptStatus);

                for (int i = 0; i < 20 &&
                     session.PlayerSnapshot!.PublicView.Board.Phase != GamePhase.RoundEnd; i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }

                Assert.Equal(GamePhase.RoundEnd, session.PlayerSnapshot!.PublicView.Board.Phase);

                RoundSettlementCalculation? kanCalc = ((LocalTableChannel)channel).RuntimeForMatch?.ActorForTest.LastSettlementCalculation;
                Assert.NotNull(kanCalc);
                Assert.Equal(RoundOutcomeKind.Tsumo, kanCalc.Outcome.Kind);
                Assert.Equal(0, kanCalc.Outcome.WinnerSeat);
                Assert.NotNull(kanCalc.Breakdown);
                Assert.Equal(10, kanCalc.Breakdown.TotalYakuHan);
                Assert.Equal(60, kanCalc.Breakdown.Fu.FinalFu);
                Assert.Equal(24000L, kanCalc.Breakdown.TotalPoints);
                Assert.Contains(kanCalc.Breakdown.Yaku, y => y.Yaku.Code == "RINSHAN");
                Assert.Contains(kanCalc.Breakdown.Yaku, y => y.Yaku.Code == "TOITOI");
                Assert.Contains(kanCalc.Breakdown.Yaku, y => y.Yaku.Code == "SAN_ANKOU");
                Assert.Contains(kanCalc.Breakdown.Yaku, y => y.Yaku.Code == "CHINITSU");
                Assert.Equal(24000L, kanCalc.Payments.Deltas[0]);
                Assert.Equal(-8000L, kanCalc.Payments.Deltas[1]);
                Assert.Equal(-8000L, kanCalc.Payments.Deltas[2]);
                Assert.Equal(-8000L, kanCalc.Payments.Deltas[3]);

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task ChankanRonCandidateAndSubmissionTest()
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
            int[] seat0Tiles = { 4, 8, 16, 20, 24, 40, 44, 48, 76, 80, 100, 101, 130, 132 };
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
            specific[57] = 84;  // Seat 0 turn 2 draw -> tile 84 (4s) completes 2-3-4s! Seat 0 enters Tenpai waiting on 1m/4m! Discards 130!
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
                PlayerSnapshot turn2Snapshot = await WaitForActionTypeVersionAsync(
                    session,
                    ProtocolActionType.DiscardTile,
                    pumpCts.Token,
                    snapshot.StateVersion + 1);
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
                Assert.Equal(2, chankanCalc.Breakdown.TotalYakuHan);
                Assert.Equal(30, chankanCalc.Breakdown.Fu.FinalFu);
                Assert.Equal(2900L, chankanCalc.Breakdown.TotalPoints);
                Assert.Contains(chankanCalc.Breakdown.Yaku, y => y.Yaku.Code == "CHANKAN");
                Assert.Contains(chankanCalc.Breakdown.Yaku, y => y.Yaku.Code == "PINFU");
                Assert.Equal(2900L, chankanCalc.Payments.Deltas[0]);
                Assert.Equal(-2900L, chankanCalc.Payments.Deltas[1]);
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

        [Fact]
        public async Task MultipleChiOptionsCandidateTest()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Seat 0 hand has: 1m (0), 2m (4), 4m (12), 5m (16) and discard tile 132.
            // When Seat 3 discards 3m (8), Seat 0 has 3 distinct Chi combinations:
            // 1) 1m-2m-3m (hand 0, 4)
            // 2) 2m-3m-4m (hand 4, 12)
            // 3) 3m-4m-5m (hand 12, 16)
            var specific = new Dictionary<int, int>();
            int[] seat0Tiles = { 0, 4, 12, 16, 20, 24, 28, 36, 40, 44, 48, 52, 56, 132 };
            int[] seat0Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            for (int i = 0; i < seat0Slots.Length; i++)
            {
                specific[seat0Slots[i]] = seat0Tiles[i];
            }

            specific[53] = 133; // Seat 1 draw
            specific[54] = 134; // Seat 2 draw
            specific[55] = 8;   // Seat 3 draw: 3m (8)
            specific[100] = 9;
            specific[101] = 10;
            specific[102] = 11;

            harness.Options.InitialRound = BuildRoundWithSpecificSlots(specific);

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(25));
                Task pump = StartPump(session, pumpCts.Token);

                PlayerSnapshot snapshot = await WaitForDecisionAsync(session, pumpCts.Token);
                DecisionView decision = snapshot.PrivateView.Decision!;

                var discardResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-multi-chi-discard")
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
                    new RequestId("seat0-multi-chi-discard"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snapshot.StateVersion),
                    new RoundId(snapshot.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));

                await session.SubmitAsync(discardCommand, pumpCts.Token);
                await LocalHostCommandTests.WithTimeoutAsync(discardResolved.Task, pumpCts.Token);

                PlayerSnapshot chiSnapshot = await WaitForActionTypeAsync(
                    session,
                    ProtocolActionType.DeclareChi,
                    pumpCts.Token);
                Assert.NotNull(chiSnapshot.PrivateView.Decision);
                DecisionView chiDecision = chiSnapshot.PrivateView.Decision;

                var chiActions = chiDecision.Actions.Where(a => a.ActionType == (uint)ProtocolActionType.DeclareChi).ToList();
                Assert.True(chiActions.Count >= 2, "Expected at least 2 Chi candidates, got " + chiActions.Count);

                // Verify that distinct options have distinct entity combinations
                var optionTileSets = chiActions.Select(a => string.Join(",", a.TileIds.OrderBy(x => x))).Distinct().ToList();
                Assert.Equal(chiActions.Count, optionTileSets.Count);

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task RiichiAfterClosedKanClientSubmissionTest()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Hand: 4x 1m (0, 1, 2, 3), 1-9p (36, 40, 44, 48, 52, 56, 60, 64, 68), 1s (72).
            // Rinshan slot 122 (DeadWall(0)) is 132 (Chun).
            int[] handTiles = { 0, 1, 2, 3, 36, 40, 44, 48, 52, 56, 60, 64, 68, 72 };
            int[] dealerSlots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            var specificSlots = new Dictionary<int, int>();
            for (int i = 0; i < dealerSlots.Length; i++)
            {
                specificSlots[dealerSlots[i]] = handTiles[i];
            }

            specificSlots[122] = 132; // Rinshan draw

            harness.Options.InitialRound = BuildRoundWithSpecificSlots(specificSlots);

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(25));
                Task pump = StartPump(session, pumpCts.Token);

                PlayerSnapshot snapshot = await WaitForDecisionAsync(session, pumpCts.Token);
                Assert.NotNull(snapshot.PrivateView.Decision);
                DecisionView decision = snapshot.PrivateView.Decision;

                LegalActionView? kanAction = decision.Actions.FirstOrDefault(
                    a => a.ActionType == (uint)ProtocolActionType.DeclareKan &&
                         a.OptionId.StartsWith("closed-kan-", StringComparison.Ordinal));
                Assert.NotNull(kanAction);

                // 1. Submit ClosedKan
                var kanResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "closed-kan-riichi-1")
                    {
                        kanResolved.TrySetResult(args);
                    }
                };

                BusinessPayload kanPayload = CommandPayloadFactory.SubmitAction(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    ProtocolActionType.DeclareKan,
                    optionId: kanAction.OptionId,
                    tileIds: kanAction.TileIds.ToArray(),
                    meldType: 4u);
                var kanCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    kanPayload,
                    new RequestId("closed-kan-riichi-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snapshot.StateVersion),
                    new RoundId(snapshot.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));

                await session.SubmitAsync(kanCommand, pumpCts.Token);
                TableReceiptResolvedEventArgs kanReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                    kanResolved.Task,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, kanReceipt.Resolution.ReceiptStatus);

                // 2. Rinshan draw draws 132; client receives DeclareRiichi candidate!
                PlayerSnapshot? riichiSnapshot = null;
                for (int i = 0; i < 50; i++)
                {
                    PlayerSnapshot? s = session.PlayerSnapshot;
                    if (s?.PrivateView?.Decision != null &&
                        s.StateVersion > snapshot.StateVersion &&
                        s.PrivateView.Decision.Actions.Any(a => a.OptionId == "riichi-132"))
                    {
                        riichiSnapshot = s;
                        break;
                    }
                    await Task.Delay(20, pumpCts.Token);
                }
                Assert.NotNull(riichiSnapshot);
                DecisionView riichiDecision = riichiSnapshot.PrivateView.Decision!;
                LegalActionView? riichiAction = riichiDecision.Actions.FirstOrDefault(
                    a => a.ActionType == (uint)ProtocolActionType.DeclareRiichi &&
                         a.OptionId == "riichi-132");
                Assert.NotNull(riichiAction);
                Assert.Contains(132u, riichiAction.TileIds);

                // 3. Submit DeclareRiichi discarding tile 132
                var riichiResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "riichi-after-closed-kan-1")
                    {
                        riichiResolved.TrySetResult(args);
                    }
                };

                BusinessPayload riichiPayload = CommandPayloadFactory.SubmitAction(
                    riichiSnapshot.RoundId,
                    riichiDecision.DecisionId,
                    riichiDecision.ControlEpoch,
                    ProtocolActionType.DeclareRiichi,
                    optionId: riichiAction.OptionId,
                    tileIds: new uint[] { 132u });
                var riichiCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    riichiPayload,
                    new RequestId("riichi-after-closed-kan-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(session.PlayerSnapshot!.StateVersion),
                    new RoundId(riichiSnapshot.RoundId),
                    decisionId: new DecisionId(riichiDecision.DecisionId),
                    controlEpoch: new ControlEpoch(riichiDecision.ControlEpoch));

                await session.SubmitAsync(riichiCommand, pumpCts.Token);
                TableReceiptResolvedEventArgs riichiReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                    riichiResolved.Task,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, riichiReceipt.Resolution.ReceiptStatus);

                // Verify Riichi took effect in public board view and round state
                for (int i = 0; i < 20 && !session.PlayerSnapshot!.PublicView.Board.Players[0].Riichi; i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }
                Assert.True(session.PlayerSnapshot!.PublicView.Board.Players[0].Riichi);
                RoundStateMachine currentRound = ((LocalTableChannel)channel).RuntimeForMatch!.ActorForTest.GetRoundStateForHost();
                Assert.True(currentRound.IsRiichi(0));
                Assert.Equal(24000L, currentRound.Scores[0]); // 25000 - 1000 riichi deposit

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task NoYakuAiDoesNotGetStuckOnIllegalRonTest()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Seat 0 (human) has tile 52 (5p) and discards it on turn 1.
            // Seat 1 (AI) holds: 123m 456p 789s 234p EE, waiting on 5p.
            // Shape is 4 melds + 1 honor pair (EE prevents pinfu, no other yaku -> no yaku!).
            int[] seat0Tiles = { 52, 4, 8, 12, 16, 20, 24, 28, 32, 60, 64, 68, 72, 76 };
            int[] seat1Tiles = { 0, 1, 2, 48, 56, 96, 100, 104, 40, 44, 49, 108, 109 };
            int[] seat0Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            int[] seat1Slots = { 4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 43 };

            var specific = new Dictionary<int, int>();
            for (int i = 0; i < seat0Slots.Length; i++) specific[seat0Slots[i]] = seat0Tiles[i];
            for (int i = 0; i < seat1Slots.Length; i++) specific[seat1Slots[i]] = seat1Tiles[i];

            // Seat 1 draws 130 on turn 1 draw (slot 53)
            specific[53] = 130;

            harness.Options.InitialRound = BuildRoundWithSpecificSlots(specific);

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(25));
                Task pump = StartPump(session, pumpCts.Token);

                // Seat 0 discards tile 52 (which completes seat 1's hand shape, but seat 1 has no yaku)
                PlayerSnapshot snapshot = await WaitForDecisionAsync(session, pumpCts.Token);
                DecisionView decision = snapshot.PrivateView.Decision!;

                var discardResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-discard-5p")
                    {
                        discardResolved.TrySetResult(args);
                    }
                };

                BusinessPayload discardPayload = CommandPayloadFactory.SubmitAction(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    ProtocolActionType.DiscardTile,
                    tileIds: new uint[] { 52u });
                var discardCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    discardPayload,
                    new RequestId("seat0-discard-5p"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snapshot.StateVersion),
                    new RoundId(snapshot.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));

                await session.SubmitAsync(discardCommand, pumpCts.Token);
                TableReceiptResolvedEventArgs discardReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                    discardResolved.Task,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, discardReceipt.Resolution.ReceiptStatus);
                // debug removed

                // Seat 1 (AI) must NOT Ron (no yaku). Instead, AI takes its normal turn, draws 130 and discards it smoothly!
                // Wait for Seat 1 to have discarded a tile without getting stuck or entering illegal terminal ron
                bool aiTurnAdvanced = false;
                for (int i = 0; i < 50; i++)
                {
                    PlayerSnapshot? current = session.PlayerSnapshot;
                    if (current != null && current.PublicView.Board.Players[1].Discards.Count > 0)
                    {
                        aiTurnAdvanced = true;
                        break;
                    }
                    await Task.Delay(50, pumpCts.Token);
                }

                Assert.True(aiTurnAdvanced, "AI smoothly completed turn without getting stuck on illegal Ron.");
                Assert.NotEqual(GamePhase.RoundEnd, session.PlayerSnapshot!.PublicView.Board.Phase);

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task ChiOrPonAfterCallDoesNotOfferTsumoAndDiscardSucceedsTest()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Dealer = 1 (Seat 1 is AI East, Seat 0 is human South).
            // Seat 0 initial hand: [124, 125, 0, 4, 8, 12, 16, 20, 24, 28, 32, 108, 109]
            // Slots for Seat 0 when dealer = 1: [12, 13, 14, 15, 28, 29, 30, 31, 44, 45, 46, 47, 51]
            int[] seat0Tiles = { 124, 125, 0, 4, 8, 12, 16, 20, 24, 28, 32, 108, 109 };
            int[] seat0Slots = { 12, 13, 14, 15, 28, 29, 30, 31, 44, 45, 46, 47, 51 };

            var specific = new Dictionary<int, int>();
            for (int i = 0; i < seat0Slots.Length; i++)
            {
                specific[seat0Slots[i]] = seat0Tiles[i];
            }

            // Slot 52 is the dealer's 14th tile. Dealer is Seat 1.
            // Seat 1 draws 126 and discards 126 on turn 1!
            specific[52] = 126;

            harness.Options.InitialRound = BuildRoundWithSpecificSlots(specific, dealer: 1);

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(25));
                Task pump = StartPump(session, pumpCts.Token);

                // 1. Seat 1 (AI dealer) discards 126. Seat 0 (human) receives DeclarePon!
                PlayerSnapshot ponSnapshot = await WaitForActionTypeAsync(
                    session,
                    ProtocolActionType.DeclarePon,
                    pumpCts.Token);
                Assert.NotNull(ponSnapshot.PrivateView.Decision);
                DecisionView ponDecision = ponSnapshot.PrivateView.Decision;

                LegalActionView? ponAction = ponDecision.Actions.FirstOrDefault(
                    a => a.ActionType == (uint)ProtocolActionType.DeclarePon);
                Assert.NotNull(ponAction);

                var ponResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-pon-white-1")
                    {
                        ponResolved.TrySetResult(args);
                    }
                };

                BusinessPayload ponPayload = CommandPayloadFactory.SubmitAction(
                    ponSnapshot.RoundId,
                    ponDecision.DecisionId,
                    ponDecision.ControlEpoch,
                    ProtocolActionType.DeclarePon,
                    tileIds: ponAction.TileIds.ToArray());
                var ponCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    ponPayload,
                    new RequestId("seat0-pon-white-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(session.PlayerSnapshot!.StateVersion),
                    new RoundId(ponSnapshot.RoundId),
                    decisionId: new DecisionId(ponDecision.DecisionId),
                    controlEpoch: new ControlEpoch(ponDecision.ControlEpoch));

                await session.SubmitAsync(ponCommand, pumpCts.Token);
                TableReceiptResolvedEventArgs ponReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                    ponResolved.Task,
                    pumpCts.Token);
                Assert.Equal(ReceiptStatus.Applied, ponReceipt.Resolution.ReceiptStatus);

                // 2. Seat 0 is now in Discard phase with concealed hand: 1-9m + Hatsu pair.
                // Although the hand shape looks like a winning hand (CanWin=true), DrawnTile is null!
                // CRITICAL ASSERTION: Must NOT offer DeclareTsumo! The pon receipt and the
                // post-call PUSH are separate; require a new decision at/after the pon
                // command version so a stale pre-call snapshot cannot satisfy this block.
                Assert.True(ponReceipt.Resolution.CommandVersion.HasValue, "pon receipt has no command version.");
                Assert.True(ponReceipt.Resolution.EventSeq.HasValue, "pon receipt has no event seq.");
                PlayerSnapshot afterPonSnapshot = await LocalHostCommandTests.WaitForPlayerSnapshotAsync(
                    session,
                    candidate => HasNewDecisionAfter(
                        candidate,
                        ponSnapshot.RoundId,
                        ponDecision.DecisionId,
                        ponReceipt.Resolution.CommandVersion!.Value.Value,
                        ponReceipt.Resolution.EventSeq!.Value.Value,
                        ProtocolActionType.DiscardTile),
                    "seat0 discard decision after seat0-pon-white-1",
                    pumpCts.Token);
                Assert.NotNull(afterPonSnapshot.PrivateView.Decision);
                DecisionView afterPonDecision = afterPonSnapshot.PrivateView.Decision!;
                Assert.NotEqual(ponDecision.DecisionId, afterPonDecision.DecisionId);
                Assert.True(
                    afterPonSnapshot.StateVersion >= ponReceipt.Resolution.CommandVersion!.Value.Value,
                    "post-pon snapshot regressed below the applied pon command version.");

                Assert.DoesNotContain(afterPonDecision.Actions,
                    a => a.ActionType == (uint)ProtocolActionType.DeclareTsumo);
                Assert.Contains(afterPonDecision.Actions,
                    a => a.ActionType == (uint)ProtocolActionType.DiscardTile);

                // 3. Submitting one of the DiscardTile candidates succeeds!
                LegalActionView discardAction = afterPonDecision.Actions.First(
                    a => a.ActionType == (uint)ProtocolActionType.DiscardTile && a.TileIds.Contains(109u));

                var discardResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "seat0-discard-after-call")
                    {
                        discardResolved.TrySetResult(args);
                    }
                };

                BusinessPayload discardPayload = CommandPayloadFactory.SubmitAction(
                    afterPonSnapshot.RoundId,
                    afterPonDecision.DecisionId,
                    afterPonDecision.ControlEpoch,
                    ProtocolActionType.DiscardTile,
                    tileIds: discardAction.TileIds.ToArray());
                var discardCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    discardPayload,
                    new RequestId("seat0-discard-after-call"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(afterPonSnapshot.StateVersion),
                    new RoundId(afterPonSnapshot.RoundId),
                    decisionId: new DecisionId(afterPonDecision.DecisionId),
                    controlEpoch: new ControlEpoch(afterPonDecision.ControlEpoch));

                CommandSubmission discardSubmission = await session.SubmitAsync(discardCommand, pumpCts.Token);
                Assert.True(
                    discardSubmission.Sent,
                    "seat0-discard-after-call was not handed to the transport. " +
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
                        "seat0-discard-after-call receipt timed out; decision=" +
                        afterPonDecision.DecisionId +
                        " stateVersion=" + afterPonSnapshot.StateVersion +
                        " sent=" + discardSubmission.Sent +
                        ". " + LocalHostCommandTests.DescribeSession(session),
                        exception);
                }

                Assert.True(
                    discardReceipt.Resolution.ReceiptStatus == ReceiptStatus.Applied,
                    "seat0-discard-after-call rejected: " + discardReceipt.Resolution.ReasonCode +
                    " evaluated=" + discardReceipt.Resolution.EvaluatedStateVersion +
                    ". " + LocalHostCommandTests.DescribeSession(session));
                Assert.True(discardReceipt.Resolution.CommandVersion.HasValue, "discard receipt has no command version.");
                Assert.True(discardReceipt.Resolution.EventSeq.HasValue, "discard receipt has no event seq.");

                // Confirm Seat 0's discard is committed onto the public board at the
                // applied command version. ReceiptResolved can arrive before the PUSH
                // that carries the public board, so the assertion itself stays exact
                // while the wait is driven by the snapshot's authoritative versions.
                PlayerSnapshot discardedSnapshot = await LocalHostCommandTests.WaitForPlayerSnapshotAsync(
                    session,
                    candidate => candidate.StateVersion >= discardReceipt.Resolution.CommandVersion!.Value.Value &&
                                 candidate.EventSeq >= discardReceipt.Resolution.EventSeq!.Value.Value &&
                                 candidate.PublicView.Board.Players[0].Discards.Any(d => d.TileId == 109u),
                    "public board contains seat0 discard 109 at the applied discard version",
                    pumpCts.Token);
                Assert.Contains(discardedSnapshot.PublicView.Board.Players[0].Discards, d => d.TileId == 109u);
                Assert.False(
                    discardedSnapshot.PrivateView.Decision != null &&
                    discardedSnapshot.PrivateView.Decision.Actions.Any(
                        action => action.ActionType == (uint)ProtocolActionType.DeclareTsumo),
                    "a discard decision offered DeclareTsumo without a drawn tile.");

                RoundStateMachine roundAfterDiscard = ((LocalTableChannel)channel).RuntimeForMatch!
                    .ActorForTest.GetRoundStateForHost();
                Assert.Contains(
                    roundAfterDiscard.Tiles.TilesInZone(TileZone.Discard, 0),
                    tile => tile.Value == 109);
                ConservationReport conservation = TilePositionMap.Validate(roundAfterDiscard.Tiles);
                Assert.True(
                    conservation.IsValid,
                    "physical conservation failed after seat0-discard-after-call: " + conservation);
                Assert.Equal(136, conservation.AssignedCount);

                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task AiChiOrPonDiscardsNormallyWithoutLoopingOnIllegalTsumoTest()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Dealer = 1 (Seat 1 is AI East, Seat 2 is AI South).
            // Seat 1 starts with tile 126 at slot 52 and discards it.
            // Seat 2 (AI South) has hand with two White dragons (124, 125) and disjoint tiles.
            // Slots for Seat 2 when dealer = 1: [4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 49]
            int[] seat2Tiles = { 124, 125, 0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80 };
            int[] seat2Slots = { 4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 49 };

            var specific = new Dictionary<int, int>();
            for (int i = 0; i < seat2Slots.Length; i++)
            {
                specific[seat2Slots[i]] = seat2Tiles[i];
            }
            // Slot 52 is Seat 1's 14th tile. Seat 1 discards 126!
            specific[52] = 126;

            harness.Options.InitialRound = BuildRoundWithSpecificSlots(specific, dealer: 1);

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(25));
                Task pump = StartPump(session, pumpCts.Token);

                // Seat 1 (AI East) discards 126.
                // Seat 2 (AI South) calls Pon on 126.
                // After Pon, Seat 2 (AI) has concealed 11 tiles (CanWin=false, DrawnTile=null).
                // With our fix, AI does NOT attempt illegal Tsumo, but smoothly discards a tile!
                bool aiDiscarded = false;
                for (int i = 0; i < 60; i++)
                {
                    PlayerSnapshot? current = session.PlayerSnapshot;
                    if (current != null &&
                        current.PublicView.Board.Players[2].Melds.Count > 0 &&
                        current.PublicView.Board.Players[2].Discards.Count > 0)
                    {
                        aiDiscarded = true;
                        break;
                    }

                    await Task.Delay(50, pumpCts.Token);
                }

                Assert.True(aiDiscarded, "AI smoothly called Pon and completed its discard without attempting invalid Tsumo.");
                Assert.NotEqual(GamePhase.RoundEnd, session.PlayerSnapshot!.PublicView.Board.Phase);

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

        private static bool HasNewDecisionAfter(
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

        private static Task<PlayerSnapshot> WaitForActionTypeAsync(
            TableSession session,
            ProtocolActionType actionType,
            CancellationToken cancellationToken)
        {
            return WaitForActionTypeVersionAsync(session, actionType, cancellationToken, 0);
        }

        private static async Task<PlayerSnapshot> WaitForActionTypeVersionAsync(
            TableSession session,
            ProtocolActionType actionType,
            CancellationToken cancellationToken,
            ulong minStateVersion)
        {
            for (int i = 0; i < 50; i++)
            {
                PlayerSnapshot? snapshot = session.PlayerSnapshot;
                if (snapshot?.PrivateView?.Decision != null &&
                    snapshot.StateVersion >= minStateVersion &&
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

        private static RoundStateMachine BuildRoundWithSpecificSlots(
            IReadOnlyDictionary<int, int> specific,
            int dealer = 0)
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
            TilePositionMap dealt = TilePositionMap.CreateDealt(wall, dealer);
            var spec = new RoundSpec(
                "test-round",
                dealer,
                1,
                0,
                0,
                new long[] { 25_000L, 25_000L, 25_000L, 25_000L },
                0);
            return RoundStateMachine.CreateDealt(spec, dealt, RoundPhase.CheatWindow);
        }

    }
}
