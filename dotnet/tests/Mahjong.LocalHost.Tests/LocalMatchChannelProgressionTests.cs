#nullable enable
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
    /// <summary>
    /// Real LocalMatchCoordinator/TableSession regression for the P4.5 match
    /// assembly seam: RecordRoundCompletionAsync must advance the durable match
    /// without closing the session-owned channel, a closed channel must reopen the
    /// same durable round, and the next round must be a new durable actor.
    /// </summary>
    [Collection("LocalHostSerial")]
    public sealed class LocalMatchChannelProgressionTests
    {
        [Fact]
        public async Task RecordCompletionKeepsChannelOpenAndClosedRoundCanRecoverSameRound()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            string root = Path.Combine(harness.Root, "match-channel-progression");
            var options = new LocalMatchHostOptions
            {
                SaveDirectory = root,
                MatchId = "channel-progression",
                OwnerInstanceId = "channel-progression-owner",
                LeaseDuration = TimeSpan.FromMinutes(5),
                Environment = harness.Options.Environment,
                Rules = new MatchRuleConfig(MatchLength.Tonpuusen, handsPerWind: 2, allowWestExtension: false)
            };
            options.RoundFactory = (state, _) => BuildRiichiWinRound(
                state.CurrentRoundId,
                state.Scores,
                state.RiichiSticks);

            string firstRoundId;
            LocalMatchCoordinator match = await LocalMatchCoordinator.OpenAsync(options, ct);
            try
            {
                LocalMatchRound round = await match.OpenRoundAsync(ct);
                firstRoundId = round.RoundId;
                TableSession session = await OpenSessionAsync(round, ct);
                ProtocolCommand? ronCommand = null;
                PlayerSnapshot? terminalState = null;
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(60));
                Task pump = PumpAsync(session, pumpCts.Token);
                try
                {
                    await WaitReadyAsync(session, pumpCts.Token);
                    PlayerSnapshot riichi = await WaitForActionAsync(session, ProtocolActionType.DeclareRiichi, pumpCts.Token);
                    await SubmitActionAsync(
                        session,
                        riichi,
                        riichi.PrivateView.Decision!,
                        ProtocolActionType.DeclareRiichi,
                        "channel-riichi",
                        new uint[] { 132u },
                        riichi.PrivateView.Decision!.Actions.First(
                            a => a.ActionType == (uint)ProtocolActionType.DeclareRiichi).OptionId,
                        pumpCts.Token);
                    PlayerSnapshot ron = await WaitForActionAsync(session, ProtocolActionType.DeclareRon, pumpCts.Token);
                    ronCommand = await SubmitActionAsync(
                        session,
                        ron,
                        ron.PrivateView.Decision!,
                        ProtocolActionType.DeclareRon,
                        "channel-ron",
                        null,
                        null,
                        pumpCts.Token);
                    await WaitForPhaseAsync(session, WireGamePhase.RoundEnd, pumpCts.Token);
                    terminalState = session.PlayerSnapshot!;
                }
                finally
                {
                    pumpCts.Cancel();
                    try { await pump; } catch { }
                }

                // Close the session-owned channel before recording. The coordinator
                // must reopen the exact same durable round from its checkpoint.
                await session.CloseAsync("TEST_CLOSE_BEFORE_RECORD", CancellationToken.None);
                Assert.False(round.Channel.IsOpen);

                LocalMatchRound recovered = await match.OpenRoundAsync(ct);
                Assert.Equal(firstRoundId, recovered.RoundId);

                TableSession recoveredSession = await OpenSessionAsync(recovered, ct);
                using var recoveredPumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                recoveredPumpCts.CancelAfter(TimeSpan.FromSeconds(60));
                Task recoveredPump = PumpAsync(recoveredSession, recoveredPumpCts.Token);
                try
                {
                    await WaitReadyAsync(recoveredSession, recoveredPumpCts.Token);
                    await WaitForPhaseAsync(recoveredSession, WireGamePhase.RoundEnd, recoveredPumpCts.Token);
                    PlayerSnapshot recoveredTerminal = recoveredSession.PlayerSnapshot!;
                    Assert.NotNull(terminalState);
                    Assert.NotNull(ronCommand);
                    Assert.NotEqual(
                        ronCommand!.ScopeId!.Value,
                        recoveredSession.Binding!.Scope.ScopeId);
                    // A recovered local channel receives a new client scope. The
                    // original request cannot be replayed through the new session;
                    // fail closed instead of fabricating a new scope/request. This
                    // is the documented LocalHost/Client scope contract gap.
                    await Assert.ThrowsAsync<ArgumentException>(
                        () => recoveredSession.SubmitAsync(ronCommand, recoveredPumpCts.Token));
                    Assert.Equal(terminalState!.StateVersion, recoveredSession.PlayerSnapshot!.StateVersion);
                    Assert.Equal(terminalState.EventSeq, recoveredSession.PlayerSnapshot!.EventSeq);
                    Assert.Equal(recoveredTerminal.StateVersion, recoveredSession.PlayerSnapshot!.StateVersion);
                    LocalMatchSnapshot after = await match.RecordRoundCompletionAsync(ct);

                    // The session owner still owns the channel after durable completion.
                    Assert.True(recovered.Channel.IsOpen);
                    Assert.Single(after.Records);
                    Assert.Equal(firstRoundId, after.Records[0].RoundId);
                    Assert.False(after.Finished);

                    // Recording the same terminal round again must be rejected and
                    // must not append a second settlement/record.
                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => match.RecordRoundCompletionAsync(ct));
                    Assert.Single(match.Snapshot.Records);
                    Assert.Equal(after.Records[0].FinalScores, match.Snapshot.Records[0].FinalScores);
                }
                finally
                {
                    recoveredPumpCts.Cancel();
                    try { await recoveredPump; } catch { }
                }

                await recoveredSession.CloseAsync("TEST_COMPLETE_OWNER_CLOSE", CancellationToken.None);

                LocalMatchRound next = await match.OpenRoundAsync(ct);
                Assert.NotEqual(firstRoundId, next.RoundId);
                await next.Channel.OpenAsync(
                    new TableChannelConnectRequest(next.Ticket, "channel-progression-next", null),
                    ct);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => match.RecordRoundCompletionAsync(ct));
                await next.Channel.CloseAsync(
                    new TableChannelCloseReason("TEST_NEXT_ROUND_CLOSE", true),
                    CancellationToken.None);
            }
            finally
            {
                await match.DisposeAsync();
            }

            LocalMatchCoordinator restored = await LocalMatchCoordinator.OpenAsync(options, ct);
            try
            {
                Assert.Single(restored.Snapshot.Records);
                Assert.Equal(firstRoundId, restored.Snapshot.Records[0].RoundId);
                Assert.False(restored.Snapshot.Finished);
                Assert.NotEqual(firstRoundId, restored.Snapshot.RoundId);
            }
            finally
            {
                await restored.DisposeAsync();
            }
        }

        [Fact]
        public async Task LostReceiptIsReconciledThroughOriginalOutboxWithoutDuplicateSideEffect()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            string root = Path.Combine(harness.Root, "match-outbox-reconcile");
            var options = new LocalMatchHostOptions
            {
                SaveDirectory = root,
                MatchId = "outbox-reconcile",
                OwnerInstanceId = "outbox-reconcile-owner",
                LeaseDuration = TimeSpan.FromMinutes(5),
                Environment = harness.Options.Environment,
                Rules = new MatchRuleConfig(MatchLength.Tonpuusen, handsPerWind: 2, allowWestExtension: false)
            };
            options.RoundFactory = (state, hostOptions) =>
            {
                // Keep the recovery deterministic: only our command may advance state.
                hostOptions.AutoPlay = false;
                return BuildRiichiWinRound(state.CurrentRoundId, state.Scores, state.RiichiSticks);
            };

            var outbox = new InMemoryOutboxStore();
            LocalMatchCoordinator match = await LocalMatchCoordinator.OpenAsync(options, ct);
            try
            {
                LocalMatchRound round = await match.OpenRoundAsync(ct);
                string roundId = round.RoundId;
                await round.Channel.OpenAsync(
                    new TableChannelConnectRequest(round.Ticket, "outbox-reconcile", null), ct);
                var session = new TableSession(round.Channel, outbox);
                await session.OpenAsync(new TableSessionOpenRequest(round.Ticket, "outbox-reconcile"), ct);
                for (int i = 0; i < 100 && session.State != TableConnectionState.Ready; i++)
                {
                    await session.PumpOnceAsync(ct);
                }

                Assert.Equal(TableConnectionState.Ready, session.State);
                PlayerSnapshot baseline = session.PlayerSnapshot!;
                uint tile = baseline.PrivateView.ClosedTiles[0];
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    baseline.RoundId,
                    "local-direct",
                    0UL,
                    ProtocolActionType.DiscardTile,
                    tileIds: new[] { tile });
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    new RequestId("lost-receipt-assembly-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(baseline.StateVersion),
                    new RoundId(baseline.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(0));
                CommandSubmission submission = await session.SubmitAsync(command, ct);
                Assert.True(submission.Sent);

                // Do not pump the receipt: the side effect is durable but the
                // original scope/request remains unresolved in the same outbox.
                Assert.Single(session.PendingCommands);
                await session.CloseAsync("LOST_RECEIPT", CancellationToken.None);

                LocalMatchRound recovered = await match.OpenRoundAsync(ct);
                Assert.Equal(roundId, recovered.RoundId);
                await recovered.Channel.OpenAsync(
                    new TableChannelConnectRequest(recovered.Ticket, "outbox-reconcile", null), ct);
                var recoveredSession = new TableSession(recovered.Channel, outbox);
                await recoveredSession.OpenAsync(
                    new TableSessionOpenRequest(recovered.Ticket, "outbox-reconcile"), ct);
                for (int i = 0; i < 100 && recoveredSession.State != TableConnectionState.Ready; i++)
                {
                    await recoveredSession.PumpOnceAsync(ct);
                }

                Assert.Equal(TableConnectionState.Ready, recoveredSession.State);
                Assert.Single(recoveredSession.PendingCommands);
                PlayerSnapshot beforeReconcile = recoveredSession.PlayerSnapshot!;
                TableReceiptResolvedEventArgs? resolution = null;
                EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) =>
                {
                    if (string.Equals(args.Command.Key.RequestId.Value, command.RequestId.Value, StringComparison.Ordinal))
                    {
                        resolution = args;
                    }
                };
                recoveredSession.ReceiptResolved += handler;
                try
                {
                    await recoveredSession.ReconcilePendingAsync(ct);
                    for (int i = 0; i < 100 && recoveredSession.PendingCommands.Count > 0; i++)
                    {
                        await recoveredSession.PumpOnceAsync(ct);
                    }
                }
                finally
                {
                    recoveredSession.ReceiptResolved -= handler;
                }

                Assert.Empty(recoveredSession.PendingCommands);
                Assert.NotNull(resolution);
                Assert.Equal(ReceiptStatus.Applied, resolution!.Resolution.ReceiptStatus);
                Assert.True(resolution.Resolution.FromReceiptLookup);
                Assert.False(string.IsNullOrEmpty(resolution.Resolution.CanonicalPayloadSha256));
                PlayerSnapshot afterReconcile = recoveredSession.PlayerSnapshot!;
                Assert.Equal(beforeReconcile.StateVersion, afterReconcile.StateVersion);
                Assert.Equal(beforeReconcile.EventSeq, afterReconcile.EventSeq);

                await recoveredSession.CloseAsync("RECONCILE_DONE", CancellationToken.None);
            }
            finally
            {
                await match.DisposeAsync();
            }
        }

        private static async Task<TableSession> OpenSessionAsync(LocalMatchRound round, CancellationToken ct)
        {
            await round.Channel.OpenAsync(
                new TableChannelConnectRequest(round.Ticket, "channel-progression", null),
                ct);
            var session = new TableSession(round.Channel, new InMemoryOutboxStore());
            await session.OpenAsync(
                new TableSessionOpenRequest(round.Ticket, "channel-progression"),
                ct);
            return session;
        }

        private static async Task WaitReadyAsync(TableSession session, CancellationToken ct)
        {
            for (int i = 0; i < 200 && session.State != TableConnectionState.Ready; i++)
            {
                await Task.Delay(10, ct);
            }

            Assert.Equal(TableConnectionState.Ready, session.State);
        }

        private static async Task PumpAsync(TableSession session, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await session.PumpOnceAsync(ct);
                }
            }
            catch (Exception exception) when (
                exception is OperationCanceledException ||
                exception is TableChannelClosedException ||
                exception is InvalidOperationException)
            {
            }
        }

        private static async Task<PlayerSnapshot> WaitForActionAsync(
            TableSession session,
            ProtocolActionType action,
            CancellationToken ct)
        {
            for (int i = 0; i < 300; i++)
            {
                PlayerSnapshot? snapshot = session.PlayerSnapshot;
                if (snapshot?.PrivateView?.Decision != null &&
                    snapshot.PrivateView.Decision.Actions.Any(a => a.ActionType == (uint)action))
                {
                    return snapshot;
                }

                await Task.Delay(20, ct);
            }

            throw new TimeoutException("Timed out waiting for action " + action + ".");
        }

        private static async Task WaitForPhaseAsync(
            TableSession session,
            WireGamePhase phase,
            CancellationToken ct)
        {
            for (int i = 0; i < 300; i++)
            {
                if (session.PlayerSnapshot?.PublicView?.Board?.Phase == phase)
                {
                    return;
                }

                await Task.Delay(20, ct);
            }

            throw new TimeoutException("Timed out waiting for phase " + phase + ".");
        }

        private static async Task<ProtocolCommand> SubmitActionAsync(
            TableSession session,
            PlayerSnapshot snapshot,
            DecisionView decision,
            ProtocolActionType action,
            string requestId,
            uint[]? tiles,
            string? optionId,
            CancellationToken ct)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                PlayerSnapshot latest = await WaitForActionAsync(session, action, ct);
                DecisionView latestDecision = latest.PrivateView!.Decision!;
                LegalActionView candidate = latestDecision.Actions.First(
                    a => a.ActionType == (uint)action);
                string attemptRequestId = attempt == 0 ? requestId : requestId + "-r" + attempt;
                var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) =>
                {
                    if (string.Equals(args.Command.Key.RequestId.Value, attemptRequestId, StringComparison.Ordinal))
                    {
                        resolved.TrySetResult(args);
                    }
                };
                session.ReceiptResolved += handler;
                try
                {
                    BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                        latest.RoundId,
                        latestDecision.DecisionId,
                        latestDecision.ControlEpoch,
                        action,
                        optionId: string.IsNullOrEmpty(candidate.OptionId) ? optionId : candidate.OptionId,
                        tileIds: candidate.TileIds.Count > 0 ? candidate.TileIds.ToArray() : tiles);
                    var command = new ProtocolCommand(
                        CommandKind.SubmitAction,
                        payload,
                        new RequestId(attemptRequestId),
                        session.Binding!.Scope.ScopeId,
                        new StateVersion(latest.StateVersion),
                        new RoundId(latest.RoundId),
                        decisionId: new DecisionId(latestDecision.DecisionId),
                        controlEpoch: new ControlEpoch(latestDecision.ControlEpoch));
                    CommandSubmission submission = await session.SubmitAsync(command, ct);
                    Assert.True(submission.Sent);
                    Task finished = await Task.WhenAny(resolved.Task, Task.Delay(10000, ct));
                    Assert.True(finished == resolved.Task, "action receipt did not arrive");
                    TableReceiptResolvedEventArgs receipt = resolved.Task.Result;
                    if (receipt.Resolution.ReceiptStatus == ReceiptStatus.Applied)
                    {
                        return command;
                    }

                    Assert.True(
                        string.Equals(receipt.Resolution.ReasonCode, "STALE_STATE_VERSION", StringComparison.Ordinal) ||
                        string.Equals(receipt.Resolution.ReasonCode, "STALE_DECISION", StringComparison.Ordinal) ||
                        string.Equals(receipt.Resolution.ReasonCode, "NOT_YOUR_TURN", StringComparison.Ordinal),
                        "action rejected: " + receipt.Resolution.ReasonCode);
                }
                finally
                {
                    session.ReceiptResolved -= handler;
                }

                await Task.Delay(20, ct);
            }

            throw new TimeoutException("action never received a definitive Applied receipt");
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
