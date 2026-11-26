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
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalHostCheatTests
    {
        [Theory]
        [InlineData("ordinary")]
        [InlineData("riichi-win")]
        [InlineData("riichi-miss")]
        [InlineData("riichi-kan")]
        public async Task ConfirmedSleeveDrawUsesTheRealLocalClientAndRiichiRules(string scenario)
        {
            await using var harness = new P41Harness();
            harness.Options.ConfirmSeatZeroDraw = true;
            harness.Options.AutoPlay = false;
            const string roundId = "c04-confirmed-sleeve";
            var spec = new RoundSpec(roundId, 0, 1, 0, 0,
                new long[] { 25000, 25000, 25000, 25000 }, 0, confirmSeatZeroDraw: true);
            bool riichi = scenario != "ordinary";
            RecordedRandomDraw wall = riichi ? CreateRiichiSleeveWall(roundId, scenario) : RecordedRandomDraw.Standard(roundId);
            harness.Options.InitialRound = RoundStateMachine.CreateDealt(spec, TilePositionMap.CreateDealt(wall, 0));

            CancellationToken ct = TestContext.Current.CancellationToken;
            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pumpCts.CancelAfter(TimeSpan.FromSeconds(12));
            Task pump = Task.Run(async () =>
            {
                try { while (!pumpCts.IsCancellationRequested) await session.PumpOnceAsync(pumpCts.Token); }
                catch (OperationCanceledException) when (pumpCts.IsCancellationRequested) { }
            }, ct);
            try
            {
                LocalTableRuntime runtime = ((LocalTableChannel)channel).RuntimeForMatch!;
                var storeField = typeof(LocalTableRuntime).GetField(
                    "_store", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var store = (LocalTableStore)storeField!.GetValue(runtime)!;
                TableScopeRecord scope = store.IssueScope("confirmed-fixture", TableScopeRole.Player, P41Harness.RoomId,
                    (ulong)harness.Clock.UtcNowMs, (ulong)harness.Clock.UtcNowMs + 60000, "confirmed-fixture-scope");
                for (int i = 0; i < 4; i++)
                {
                    RoundStateMachine current = runtime.ActorForTest.GetRoundStateForHost();
                    TileId tile = current.Tiles.DrawnTile!.Value.Tile;
                    RoundCommand action = i == 0 && riichi
                        ? RoundCommand.Riichi(roundId, current.CurrentSeat, tile)
                        : RoundCommand.Discard(roundId, current.CurrentSeat, tile);
                    string key = "confirmed-prelude-" + i;
                    string? pendingId = runtime.ActorForTest.GetPendingDecisionId(current.CurrentSeat);
                    TableOperationResult accepted = await runtime.ActorForTest.SubmitAsync(new TableCommandEnvelope(
                        action, new RequestId(key), new StateVersion((ulong)i), new OwnerEpoch(1), new ControlEpoch(0),
                        pendingId == null ? (DecisionId?)null : new DecisionId(pendingId), 0L, scope.ScopeId, "ROUND",
                        TablePayloadSha256.Compute(System.Text.Encoding.UTF8.GetBytes(key))), pumpCts.Token);
                    Assert.True(accepted.Status == TableOperationStatus.Accepted, accepted.ReasonCode + " " + accepted.Detail);
                }
                runtime.DriverForTest.Start();
                while (session.PlayerSnapshot!.PublicView.Board.Phase != Mahjong.Contracts.Wire.GamePhase.Draw ||
                       session.PlayerSnapshot!.PrivateView.Decision == null)
                    await Task.Delay(10, pumpCts.Token);
                PlayerSnapshot pending = session.PlayerSnapshot!;
                Assert.Equal(Mahjong.Contracts.Wire.GamePhase.Draw, pending.PublicView.Board.Phase);
                Assert.False(pending.PrivateView.HasDrawnTileId);
                Assert.Contains(pending.PrivateView.Decision.Actions, action => action.ActionType == (uint)ProtocolActionType.DrawTile);
                Assert.Equal(120, pending.PrivateView.Cheat.CheatPoints);

                var command = new ProtocolCommand(CommandKind.SubmitSleeveBloom,
                    CommandPayloadFactory.SubmitSleeveBloom(roundId, pending.PrivateView.Decision.DecisionId, 0),
                    new RequestId("confirmed-client-bloom"), session.Binding!.Scope.ScopeId,
                    new StateVersion(pending.StateVersion), new RoundId(roundId),
                    new DecisionId(pending.PrivateView.Decision.DecisionId), new ControlEpoch(0));
                PlayerSnapshot drawn = await SubmitAndWaitAsync(session, command, pending, "paid draw", pumpCts.Token);
                Assert.Equal(110, drawn.PrivateView.Cheat.CheatPoints);
                Assert.Single(drawn.PrivateView.Cheat.LedgerEntries);
                Assert.Equal(string.Empty, drawn.PrivateView.Cheat.WindowId);
                if (scenario == "riichi-win")
                {
                    Assert.Null(drawn.PrivateView.Cheat.SleeveBloom);
                    for (int i = 0; i < 250 && session.PlayerSnapshot!.PublicView.Board.Phase != Mahjong.Contracts.Wire.GamePhase.RoundEnd; i++)
                        await Task.Delay(10, pumpCts.Token);
                    Assert.True(session.PlayerSnapshot!.PublicView.Board.Phase == Mahjong.Contracts.Wire.GamePhase.RoundEnd,
                        "phase=" + session.PlayerSnapshot.PublicView.Board.Phase +
                        " draw=" + session.PlayerSnapshot.PrivateView.DrawnTileId +
                        " version=" + session.PlayerSnapshot.StateVersion + " " +
                        runtime.DriverForTest.DescribeDriverForTest());
                    Assert.True(session.PlayerSnapshot!.PublicView.Board.Scores[0].Score > 25000);
                }
                else if (scenario == "riichi-miss")
                {
                    Assert.Null(drawn.PrivateView.Cheat.SleeveBloom);
                    while (!session.PlayerSnapshot!.PublicView.Board.Players[0].Discards.Any(d => d.TileId == 132))
                        await Task.Delay(10, pumpCts.Token);
                    Assert.Single(session.PlayerSnapshot!.PrivateView.Cheat.LedgerEntries);
                }
                else
                {
                    Assert.NotNull(drawn.PrivateView.Cheat.SleeveBloom);
                    Assert.Equal(drawn.PrivateView.DrawnTileId, drawn.PrivateView.Cheat.SleeveBloom.FirstTileId);
                    Assert.Equal(pending.PublicView.Board.WallRemaining - 1, drawn.PublicView.Board.WallRemaining);
                    if (scenario == "riichi-kan")
                        Assert.Equal(3u, drawn.PrivateView.Cheat.SleeveBloom.SecondTileId);
                }
            }
            finally
            {
                pumpCts.Cancel();
                await pump;
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }

            if (scenario == "ordinary")
            {
                harness.Clock.UtcNowMs += 500L;
                harness.Clock.MonotonicMillis += 500L;
                await using LocalTableRuntime reopened = await LocalTableRuntime.OpenAsync(harness.Options, ct);
                Assert.Single(reopened.ActorForTest.GetCheatLedgerForHost().Entries);
                Assert.True(reopened.ActorForTest.GetRoundStateForHost().Spec.ConfirmSeatZeroDraw);
                Assert.NotNull(reopened.ActorForTest.GetConfirmedSleeveOfferForHost(0));
            }
        }

        private static RecordedRandomDraw CreateRiichiSleeveWall(string roundId, string scenario)
        {
            int[] hand = scenario == "riichi-kan"
                ? new[] { 0, 1, 2, 40, 44, 48, 80, 84, 88, 124, 125, 92, 96 }
                : new[] { 124, 125, 126, 4, 8, 48, 52, 56, 92, 96, 100, 16, 17 };
            int[] slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48 };
            var fixedOrder = new Dictionary<int, int>
            {
                { 52, 133 }, { 56, 132 }, { 57, scenario == "riichi-win" ? 12 : scenario == "riichi-kan" ? 3 : 134 }
            };
            for (int i = 0; i < slots.Length; i++) fixedOrder.Add(slots[i], hand[i]);
            var remaining = new Queue<int>(Enumerable.Range(0, TileId.Count).Except(fixedOrder.Values));
            return RecordedRandomDraw.Create(roundId + "-wall",
                Enumerable.Range(0, TileId.Count)
                    .Select(slot => new TileId(fixedOrder.TryGetValue(slot, out int tile) ? tile : remaining.Dequeue())).ToArray());
        }

        [Fact]
        public async Task TileMarkIsSubmittedThroughTheClientAndProjectsPrivately()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            try
            {
                PlayerSnapshot baseline = session.PlayerSnapshot!;
                Assert.True(baseline.PrivateView.ClosedTiles.Count >= 4);
                uint[] tiles = baseline.PrivateView.ClosedTiles.Take(4).ToArray();

                var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) => resolved.TrySetResult(args);
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(10));
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

                BusinessPayload payload = CommandPayloadFactory.SubmitTileMark(
                    baseline.RoundId,
                    "local-direct",
                    0UL,
                    tiles);
                var command = new ProtocolCommand(
                    CommandKind.SubmitTileMark,
                    payload,
                    new RequestId("cheat-tilemark-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(baseline.StateVersion),
                    new RoundId(baseline.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(0));
                CommandSubmission submission = await session.SubmitAsync(command, pumpCts.Token);
                Assert.True(submission.Sent);

                TableReceiptResolvedEventArgs receipt = await LocalHostCommandTests.WithTimeoutAsync(
                    resolved.Task,
                    pumpCts.Token);
                Assert.True(
                    receipt.Resolution.ReceiptStatus == Mahjong.Contracts.ReceiptStatus.Applied,
                    "reason=" + receipt.Resolution.ReasonCode);

                for (int i = 0; i < 10 && session.PlayerSnapshot!.StateVersion == baseline.StateVersion; i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }

                Assert.True(session.PlayerSnapshot!.StateVersion > baseline.StateVersion);
                Assert.NotNull(session.PlayerSnapshot.PrivateView.Cheat);
                Assert.NotNull(session.PlayerSnapshot.PrivateView.Cheat.TileMark);
                Assert.NotEmpty(session.PlayerSnapshot.PrivateView.Cheat.TileMark.Groups);
                Assert.Equal(tiles, session.PlayerSnapshot.PrivateView.Cheat.TileMark.Groups[0].Tiles.Select(t => t.TileId).ToArray());
                Assert.True(session.PlayerSnapshot.PrivateView.Cheat.CheatPoints < 120);

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
        public async Task SleeveBloomAndBeamSwapAreSubmittedThroughTheClient()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
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

            try
            {
                PlayerSnapshot initial = session.PlayerSnapshot!;
                PlayerSnapshot afterMark = await SendCheatAsync(
                    session,
                    CommandKind.SubmitTileMark,
                    CommandPayloadFactory.SubmitTileMark(
                        initial.RoundId,
                        "local-direct",
                        0UL,
                        initial.PrivateView.ClosedTiles.Take(4).ToArray()),
                    "cheat-mark",
                    pumpCts.Token);
                Assert.True(afterMark.PrivateView.Cheat.CheatPoints < initial.PrivateView.Cheat.CheatPoints);

                await DiscardOnceAsync(session, pumpCts.Token);
                PlayerSnapshot drawn = await AdvanceToSeatZeroCheatWindowAsync(session, pumpCts.Token);

                PlayerSnapshot bloom = await SendCheatAsync(
                    session,
                    CommandKind.SubmitSleeveBloom,
                    CommandPayloadFactory.SubmitSleeveBloom(drawn.RoundId, "local-direct", 0UL),
                    "cheat-bloom",
                    pumpCts.Token);
                Assert.NotNull(bloom.PrivateView.Cheat.SleeveBloom);
                string bloomOffer = bloom.PrivateView.Cheat.SleeveBloom.OfferId;
                PlayerSnapshot bloomChoice = await SendCheatAsync(
                    session,
                    CommandKind.SubmitSleeveBloomChoice,
                    CommandPayloadFactory.SubmitSleeveBloomChoice(bloomOffer, SleeveBloomChoice.First),
                    "cheat-bloom-choice",
                    pumpCts.Token);
                Assert.Equal(bloom.PrivateView.Cheat.CheatPoints, bloomChoice.PrivateView.Cheat.CheatPoints);

                await DiscardOnceAsync(session, pumpCts.Token);
                PlayerSnapshot secondDraw = await AdvanceToSeatZeroCheatWindowAsync(session, pumpCts.Token);
                PlayerSnapshot beam = await SendCheatAsync(
                    session,
                    CommandKind.SubmitBeamSwap,
                    CommandPayloadFactory.SubmitBeamSwap(secondDraw.RoundId, "local-direct", 0UL),
                    "cheat-beam",
                    pumpCts.Token);
                Assert.NotNull(beam.PrivateView.Cheat.BeamSwap);
                string beamOffer = beam.PrivateView.Cheat.BeamSwap.OfferId;
                PlayerSnapshot beamChoice = await SendCheatAsync(
                    session,
                    CommandKind.SubmitBeamSwapChoice,
                    CommandPayloadFactory.SubmitBeamSwapChoice(beamOffer, BeamSwapChoice.KeepOriginal),
                    "cheat-beam-choice",
                    pumpCts.Token);
                Assert.Equal(beam.PrivateView.Cheat.CheatPoints, beamChoice.PrivateView.Cheat.CheatPoints);
            }
            finally
            {
                pumpCts.Cancel();
                await pump;
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        private static bool CheatWindow(PlayerSnapshot snapshot) =>
            snapshot.PublicView.Board.Phase == Mahjong.Contracts.Wire.GamePhase.CheatWindow;

        private static async Task DiscardOnceAsync(TableSession session, CancellationToken ct)
        {
            PlayerSnapshot snapshot = session.PlayerSnapshot!;
            uint tile = snapshot.PrivateView!.HasDrawnTileId
                ? snapshot.PrivateView!.DrawnTileId
                : snapshot.PrivateView!.ClosedTiles[0];
            await SendActionAsync(session, ProtocolActionType.DiscardTile, new[] { tile }, ct);
        }

        private static async Task<PlayerSnapshot> AdvanceToSeatZeroCheatWindowAsync(
            TableSession session,
            CancellationToken ct)
        {
            for (int i = 0; i < 300; i++)
            {
                PlayerSnapshot? snapshot = session.PlayerSnapshot;
                if (snapshot == null)
                {
                    await Task.Delay(10, ct);
                    continue;
                }

                if (snapshot.PublicView.Board.Phase == Mahjong.Contracts.Wire.GamePhase.CheatWindow &&
                    snapshot.PublicView.Board.CurrentSeat == 0 &&
                    snapshot.PrivateView != null)
                {
                    return snapshot;
                }

                if (snapshot.PublicView.Board.CurrentSeat == 0)
                {
                    switch (snapshot.PublicView.Board.Phase)
                    {
                        case Mahjong.Contracts.Wire.GamePhase.CheatWindow:
                        case Mahjong.Contracts.Wire.GamePhase.Discard:
                            await SendActionAsync(
                                session,
                                ProtocolActionType.DiscardTile,
                                new[]
                                {
                                    snapshot.PrivateView!.HasDrawnTileId
                                        ? snapshot.PrivateView!.DrawnTileId
                                        : snapshot.PrivateView!.ClosedTiles[0]
                                },
                                ct);
                            break;
                        case Mahjong.Contracts.Wire.GamePhase.ResponseWindow:
                        case Mahjong.Contracts.Wire.GamePhase.ChankanWindow:
                            await SendActionAsync(session, ProtocolActionType.Pass, null, ct);
                            break;
                    }
                }

                await Task.Delay(15, ct);
            }

            throw new TimeoutException("Seat 0 did not reach a CheatWindow.");
        }

        private static async Task<PlayerSnapshot> SendActionAsync(
            TableSession session,
            ProtocolActionType action,
            uint[]? tiles,
            CancellationToken ct)
        {
            PlayerSnapshot before = session.PlayerSnapshot!;
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                before.RoundId,
                "local-direct",
                0UL,
                action,
                tileIds: tiles);
            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId("cheat-action-" + Guid.NewGuid().ToString("N")),
                session.Binding!.Scope.ScopeId,
                new StateVersion(before.StateVersion),
                new RoundId(before.RoundId),
                decisionId: null,
                controlEpoch: new ControlEpoch(0));
            return await SubmitAndWaitAsync(session, command, before, "action", ct);
        }

        private static async Task<PlayerSnapshot> SendCheatAsync(
            TableSession session,
            CommandKind kind,
            BusinessPayload payload,
            string requestPrefix,
            CancellationToken ct)
        {
            PlayerSnapshot before = session.PlayerSnapshot!;
            var command = new ProtocolCommand(
                kind,
                payload,
                new RequestId(requestPrefix + "-" + Guid.NewGuid().ToString("N")),
                session.Binding!.Scope.ScopeId,
                new StateVersion(before.StateVersion),
                new RoundId(before.RoundId),
                decisionId: null,
                controlEpoch: new ControlEpoch(0));
            return await SubmitAndWaitAsync(session, command, before, requestPrefix, ct);
        }

        private static async Task<PlayerSnapshot> SubmitAndWaitAsync(
            TableSession session,
            ProtocolCommand command,
            PlayerSnapshot before,
            string label,
            CancellationToken ct)
        {
            var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var advanced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<TableReceiptResolvedEventArgs> receiptHandler = (_, args) => resolved.TrySetResult(args);
            EventHandler<TableSnapshotChangedEventArgs> snapshotHandler = (_, _) =>
            {
                if (session.PlayerSnapshot != null && session.PlayerSnapshot.StateVersion > before.StateVersion)
                {
                    advanced.TrySetResult(true);
                }
            };
            session.ReceiptResolved += receiptHandler;
            session.SnapshotChanged += snapshotHandler;
            try
            {
                CommandSubmission submission = await session.SubmitAsync(command, ct);
                Assert.True(submission.Sent, label + " was not sent");
                TableReceiptResolvedEventArgs receipt = await LocalHostCommandTests.WithTimeoutAsync(
                    resolved.Task,
                    ct);
                Assert.True(
                    receipt.Resolution.ReceiptStatus == Mahjong.Contracts.ReceiptStatus.Applied,
                    label + " was rejected: " + receipt.Resolution.ReasonCode);
                await LocalHostCommandTests.WithTimeoutAsync(advanced.Task, ct);
                return session.PlayerSnapshot!;
            }
            finally
            {
                session.ReceiptResolved -= receiptHandler;
                session.SnapshotChanged -= snapshotHandler;
            }
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
            for (int i = 0; i < 20 && session.State != TableConnectionState.Ready; i++)
            {
                await session.PumpOnceAsync(cancellationToken);
            }

            Assert.Equal(TableConnectionState.Ready, session.State);
            return (channel, session);
        }
    }
}
