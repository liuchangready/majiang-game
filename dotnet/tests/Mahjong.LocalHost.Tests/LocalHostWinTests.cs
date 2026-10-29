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

namespace Mahjong.LocalHost.Tests
{
    using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
    public sealed class LocalHostWinTests
    {
        [Fact]
        public async Task RealClientTsumoReachesTerminalAndSurvivesRestart()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            harness.Options.InitialRound = BuildWinningRound();

            (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
            ulong finalVersion;
            try
            {
                PlayerSnapshot baseline = session.PlayerSnapshot!;
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

                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    baseline.RoundId,
                    "local-direct",
                    0UL,
                    ProtocolActionType.DeclareTsumo);
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    new RequestId("tsumo-1"),
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
                    "tsumo rejected: " + receipt.Resolution.ReasonCode);

                for (int i = 0; i < 20 &&
                     session.PlayerSnapshot!.PublicView.Board.Phase != Mahjong.Contracts.Wire.GamePhase.RoundEnd; i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }

                Assert.Equal(Mahjong.Contracts.Wire.GamePhase.RoundEnd, session.PlayerSnapshot!.PublicView.Board.Phase);
                Assert.NotNull(session.PlayerSnapshot.PublicView.Session);
                Assert.True(session.PlayerSnapshot.PublicView.Session.FinalScores.Count == 4);
                finalVersion = session.PlayerSnapshot.StateVersion;
                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }

            using (LocalTableStore store = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                TableCheckpoint cp = (await store.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                Assert.NotNull(cp.CommittedFactsPayload);
                Assert.True(cp.CommittedFactsPayload!.Length > 0);
            }

            (ITableChannel reopened, TableSession restored) = await OpenAsync(harness, ct);
            try
            {
                Assert.Equal(finalVersion, restored.PlayerSnapshot!.StateVersion);
                Assert.Equal(
                    Mahjong.Contracts.Wire.GamePhase.RoundEnd,
                    restored.PlayerSnapshot.PublicView.Board.Phase);
            }
            finally
            {
                await restored.CloseAsync("TEST_DONE", CancellationToken.None);
                await reopened.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        private static RoundStateMachine BuildWinningRound()
        {
            int[] targets = { 0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 16, 17 };
            int[] dealerSlots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            var used = new HashSet<int>(targets);
            var remaining = new Queue<int>(Enumerable.Range(0, TileId.Count).Where(id => !used.Contains(id)));
            var order = new int[TileId.Count];
            var assigned = new bool[TileId.Count];
            for (int i = 0; i < dealerSlots.Length; i++)
            {
                order[dealerSlots[i]] = targets[i];
                assigned[dealerSlots[i]] = true;
            }

            for (int i = 0; i < order.Length; i++)
            {
                if (!assigned[i])
                {
                    order[i] = remaining.Dequeue();
                }
            }

            var tiles = order.Select(id => new TileId(id)).ToArray();
            RecordedRandomDraw wall = RecordedRandomDraw.Create("win-wall", tiles);
            TilePositionMap dealt = TilePositionMap.CreateDealt(wall, 0);
            var spec = new RoundSpec(
                "win-round",
                0,
                1,
                0,
                0,
                new long[] { 25_000L, 25_000L, 25_000L, 25_000L },
                0);
            return RoundStateMachine.CreateDealt(spec, dealt, RoundPhase.CheatWindow);
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
        }    }
}
