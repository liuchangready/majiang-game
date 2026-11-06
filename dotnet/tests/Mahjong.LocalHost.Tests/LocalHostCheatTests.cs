using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Xunit;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalHostCheatTests
    {
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
