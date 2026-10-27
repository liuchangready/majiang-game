using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain;
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
                pumpCts.CancelAfter(TimeSpan.FromSeconds(10));
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
                pumpCts.CancelAfter(TimeSpan.FromSeconds(10));
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
                pumpCts.CancelAfter(TimeSpan.FromSeconds(10));
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
                    tileIds: new uint[] { 0u, 1u, 2u, 3u },
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
                pumpCts.CancelAfter(TimeSpan.FromSeconds(10));
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

                BusinessPayload ponPayload = CommandPayloadFactory.SubmitAction(
                    ponSnapshot.RoundId,
                    ponDecision.DecisionId,
                    ponDecision.ControlEpoch,
                    ProtocolActionType.DeclarePon,
                    tileIds: new uint[] { 2u, 0u, 1u });
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

            harness.Options.InitialRound = BuildRoundWithSpecificSlots(specific);

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(10));
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

                BusinessPayload chiPayload = CommandPayloadFactory.SubmitAction(
                    chiSnapshot.RoundId,
                    chiDecision.DecisionId,
                    chiDecision.ControlEpoch,
                    ProtocolActionType.DeclareChi,
                    tileIds: new uint[] { 8u, 0u, 4u });
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
                pumpCts.CancelAfter(TimeSpan.FromSeconds(10));
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

                BusinessPayload kanPayload = CommandPayloadFactory.SubmitAction(
                    kanSnapshot.RoundId,
                    kanDecision.DecisionId,
                    kanDecision.ControlEpoch,
                    ProtocolActionType.DeclareKan,
                    tileIds: new uint[] { 3u, 0u, 1u, 2u },
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
                pumpCts.CancelAfter(TimeSpan.FromSeconds(10));
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
