using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.Domain.Rules;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// P5.4b-B2-T focused service tests. Every case starts the REAL Kestrel host, the REAL
    /// actor/commit/durable composition and the production wire mapper/handler. Settlement
    /// values come from the frozen <c>RoundSettlementCalculator</c>; clients never send
    /// scores, yaku, fu or payments.
    /// </summary>
    public sealed class B2TerminalSettlementTests
    {
        // A dealer tsumo: 111z (Haku) + 23m waiting on 4m + 345p + 678s + 55m.
        // The drawn 4m completes the hand and the Haku triplet is a guaranteed yaku.
        private static RoundStateMachine DealerTsumoRound(string roomId)
        {
            var fixture = new N1aTable { Dealer = 0 };
            fixture.Hand(0, new[]
            {
                new TileId(124), new TileId(125), new TileId(126),
                new TileId(4), new TileId(8),
                new TileId(48), new TileId(52), new TileId(56),
                new TileId(92), new TileId(96), new TileId(100),
                new TileId(16), new TileId(17)
            });
            fixture.Drawn(0, new TileId(12));
            return fixture.Build(roomId + "-r1");
        }

        // Seat 1 (dealer) discards 1m; seat 0's 13-tile hand waits on 1m/4m for a Haku ron.
        private static RoundStateMachine SeatOneDiscardsIntoSeatZeroRon(string roomId)
        {
            var fixture = new N1aTable { Dealer = 1 };
            fixture.Hand(0, new[]
            {
                new TileId(124), new TileId(125), new TileId(126),
                new TileId(4), new TileId(8),
                new TileId(48), new TileId(52), new TileId(56),
                new TileId(92), new TileId(96), new TileId(100),
                new TileId(16), new TileId(17)
            });
            fixture.Hand(1, new TileId(0));
            fixture.Drawn(1, new TileId(127));
            return fixture.Build(roomId + "-r1");
        }

        [Fact]
        public async Task DealerTsumo_GoesThroughRealMapperActorAndRoundEnd_WithResultAndNoClientSettlementBody()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(DealerTsumoRound),
                clock: FrozenUtcClock.Instance);
            TableTestClient client = await host.ConnectPlayerAsync("room-b2-tsumo", "acct-b2-tsumo", 0);
            try
            {
                PlayerSnapshot before = await WaitForDecisionAsync(client, "room-b2-tsumo-r1");
                Contracts.Wire.DecisionView decision = before.PrivateView!.Decision;
                Assert.Contains(decision.Actions, a => a.ActionType == (uint)ProtocolActionType.DiscardTile);
                Assert.Contains(
                    decision.Actions,
                    a => a.ActionType == (uint)ProtocolActionType.DeclareTsumo);
                ulong versionBefore = before.StateVersion;
                int settlementEventsBefore = CountSettlementEvents(before);

                string requestId = TableWire.RandomId("req-b2-tsumo");
                // The client names ONLY its action and identity: no han/fu/score/final-score
                // field exists in the frozen payload writer.
                byte[] payload = TableWire.SubmitActionPayload(
                    before.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    (uint)ProtocolActionType.DeclareTsumo,
                    new uint[] { 12 });
                await client.SendAsync(TableWire.Command(
                    client.Welcome!,
                    requestId,
                    Contracts.CommandKind.SubmitAction,
                    payload,
                    expectedStateVersion: versionBefore));

                Envelope receipt = await client.ReadUntilAsync(
                    e => (e.MessageType == MessageType.Receipt && e.RequestId == requestId) ||
                         e.MessageType == MessageType.Error,
                    TimeSpan.FromSeconds(20));
                Assert.True(
                    receipt.MessageType == MessageType.Receipt,
                    "the tsumo settlement returned " + receipt.MessageType + ": " +
                    (receipt.Error?.Code ?? string.Empty) + "/" + (receipt.Error?.DetailCode ?? string.Empty));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, receipt.Receipt.Status);
                Assert.True(receipt.Receipt.CommandVersion >= versionBefore + 1UL);

                PlayerSnapshot after = await WaitForPhaseAsync(client, GamePhase.RoundEnd);
                Assert.Equal(versionBefore + 1UL, after.StateVersion);
                Assert.Equal(GamePhase.RoundEnd, after.PublicView!.Board.Phase);
                Assert.Equal(before.RoundId, after.PublicView.SettlementRoundId);
                Assert.Contains(
                    after.PublicView.SettlementEvents,
                    e => e.EventType == EventType.TsumoWin);
                PublicEventEnvelope win = after.PublicView.SettlementEvents.First(e => e.EventType == EventType.TsumoWin);
                Assert.Equal(0, win.TsumoWin.Player);
                Assert.True(win.TsumoWin.Han > 0, "the frozen calculator settled a real han value");
                Assert.True(win.TsumoWin.Score > 0, "the frozen calculator settled a real score");
                Assert.Equal(100000L, after.PublicView.Board.Scores.Sum(s => s.Score));
                Assert.False(after.PublicView.Session.Finished, "match lifecycle is still NOT implemented");
                Assert.True(
                    CountSettlementEvents(after) > settlementEventsBefore,
                    "the terminal projection carries the settlement event");

                // The same durable in-memory commit wrote exactly one terminal outbox row.
                // Its payload is decoded and compared with the committed result/final scores;
                // later ticks must not add a second row or change the terminal version.
                InMemoryTableStore store = await AssertSingleTerminalOutboxAsync(
                    host, "room-b2-tsumo", after);
                await Task.Delay(400);
                Assert.Equal(1, store.OutboxCount);
                await using TableTestClient observer =
                    await host.ConnectSpectatorAsync("room-b2-tsumo", "acct-b2-tsumo-observer");
                Envelope laterEnvelope = await observer.ReadUntilAsync(
                    e => e.PublicUpdate?.Snapshot != null,
                    TimeSpan.FromSeconds(5));
                Assert.Equal(after.StateVersion, laterEnvelope.PublicUpdate.Snapshot.StateVersion);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        [Fact]
        public async Task Ron_FromTrustedWindow_GoesThroughRealMapperActorAndRoundEnd_WithFinalScores()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(SeatOneDiscardsIntoSeatZeroRon),
                clock: FrozenUtcClock.Instance);
            TableTestClient seat1 = await host.ConnectPlayerAsync("room-b2-ron", "acct-b2-ron-1", 1);
            TableTestClient seat0 = await host.ConnectPlayerAsync("room-b2-ron", "acct-b2-ron-0", 0);
            try
            {
                PlayerSnapshot dealer = await WaitForDecisionAsync(seat1, "room-b2-ron-r1");
                Contracts.Wire.DecisionView discardDecision = dealer.PrivateView!.Decision;
                var discard = discardDecision.Actions.First(
                    a => a.ActionType == (uint)ProtocolActionType.DiscardTile &&
                         a.TileIds.Contains(0u));
                await seat1.SendAsync(TableWire.Command(
                    seat1.Welcome!,
                    TableWire.RandomId("req-b2-ron-discard"),
                    Contracts.CommandKind.SubmitAction,
                    TableWire.SubmitActionPayload(
                        dealer.RoundId,
                        discardDecision.DecisionId,
                        discardDecision.ControlEpoch,
                        (uint)ProtocolActionType.DiscardTile,
                        new uint[] { 0 }),
                    expectedStateVersion: dealer.StateVersion));
                await seat1.ReadUntilAsync(
                    e => e.MessageType == MessageType.Receipt,
                    TimeSpan.FromSeconds(20));

                PlayerSnapshot ronSnapshot = await WaitForDecisionAsync(seat0, "room-b2-ron-r1");
                Contracts.Wire.DecisionView ronDecision = ronSnapshot.PrivateView!.Decision;
                Assert.Contains(ronDecision.Actions, a => a.ActionType == (uint)ProtocolActionType.DeclareRon);
                ulong versionBefore = ronSnapshot.StateVersion;

                string requestId = TableWire.RandomId("req-b2-ron");
                await seat0.SendAsync(TableWire.Command(
                    seat0.Welcome!,
                    requestId,
                    Contracts.CommandKind.SubmitAction,
                    TableWire.SubmitActionPayload(
                        ronSnapshot.RoundId,
                        ronDecision.DecisionId,
                        ronDecision.ControlEpoch,
                        (uint)ProtocolActionType.DeclareRon,
                        new uint[] { 0 }),
                    expectedStateVersion: versionBefore));
                Envelope receipt = await seat0.ReadUntilAsync(
                    e => e.MessageType == MessageType.Receipt && e.RequestId == requestId,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, receipt.Receipt.Status);

                PlayerSnapshot after = await WaitForPhaseAsync(seat0, GamePhase.RoundEnd);
                Assert.Equal(versionBefore + 1UL, after.StateVersion);
                Assert.Contains(
                    after.PublicView!.SettlementEvents,
                    e => e.EventType == EventType.RonWin);
                PublicEventEnvelope win = after.PublicView.SettlementEvents.First(e => e.EventType == EventType.RonWin);
                Assert.Equal(0, win.RonWin.Winner);
                Assert.Equal(1, win.RonWin.Loser);
                Assert.True(win.RonWin.Han > 0);
                Assert.Equal(100000L, after.PublicView.Board.Scores.Sum(s => s.Score));
                await AssertSingleTerminalOutboxAsync(host, "room-b2-ron", after);
            }
            finally
            {
                await seat0.DisposeAsync();
                await seat1.DisposeAsync();
            }
        }

        [Fact]
        public async Task NaturalWallExhaustion_ServiceCommitsOneTrustedExhaustiveDraw_AndRepeatedTicksDoNotRepublish()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 20; },
                roundSource: new FixedRoundSource(roomId =>
                {
                    var fixture = new N1aTable { Dealer = 0 };
                    fixture.Hand(0, new[]
                    {
                        new TileId(124), new TileId(125), new TileId(126),
                        new TileId(4), new TileId(8),
                        new TileId(48), new TileId(52), new TileId(56),
                        new TileId(92), new TileId(96), new TileId(100),
                        new TileId(16), new TileId(17)
                    });
                    fixture.Drawn(0, new TileId(12));
                    return fixture.Build(roomId + "-r1");
                }),
                clock: FrozenUtcClock.Instance);
            var clients = new List<TableTestClient>();
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    clients.Add(await host.ConnectPlayerAsync("room-b2-wall", "acct-b2-wall-" + seat, seat));
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(180);
                PlayerSnapshot terminal = null!;
                int actions = 0;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    bool didWork = false;
                    for (int seat = 0; seat < clients.Count; seat++)
                    {
                        PlayerSnapshot snapshot;
                        try
                        {
                            snapshot = await ReadSnapshotAsync(clients[seat], TimeSpan.FromMilliseconds(800));
                        }
                        catch (TimeoutException)
                        {
                            continue;
                        }
                        catch (OperationCanceledException)
                        {
                            continue;
                        }

                        if (snapshot.PublicView?.Board.Phase == GamePhase.RoundEnd)
                        {
                            terminal = snapshot;
                            break;
                        }

                        Contracts.Wire.DecisionView? decision = snapshot.PrivateView?.Decision;
                        if (decision == null || !seen.Add(decision.DecisionId))
                        {
                            continue;
                        }

                        Contracts.Wire.LegalActionView? discard = decision.Actions.FirstOrDefault(
                            a => a.ActionType == (uint)ProtocolActionType.DiscardTile &&
                                 a.TileIds.Count > 0);
                        Contracts.Wire.LegalActionView? pass = decision.Actions.FirstOrDefault(
                            a => a.ActionType == (uint)ProtocolActionType.Pass);
                        Contracts.Wire.LegalActionView? selected = discard ?? pass;
                        if (selected == null)
                        {
                            continue;
                        }

                        uint actionType = discard != null
                            ? (uint)ProtocolActionType.DiscardTile
                            : (uint)ProtocolActionType.Pass;
                        uint[]? tiles = discard != null ? new[] { discard.TileIds[0] } : null;
                        string requestId = TableWire.RandomId("req-b2-wall");
                        await clients[seat].SendAsync(TableWire.Command(
                            clients[seat].Welcome!,
                            requestId,
                            Contracts.CommandKind.SubmitAction,
                            TableWire.SubmitActionPayload(
                                snapshot.RoundId,
                                decision.DecisionId,
                                decision.ControlEpoch,
                                actionType,
                                tiles),
                            expectedStateVersion: snapshot.StateVersion));
                        Envelope receipt = await clients[seat].ReadUntilAsync(
                            e => e.MessageType == MessageType.Receipt && e.RequestId == requestId,
                            TimeSpan.FromSeconds(20));
                        Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, receipt.Receipt.Status);
                        actions++;
                        didWork = true;
                    }

                    if (terminal != null)
                    {
                        break;
                    }

                    if (!didWork)
                    {
                        await Task.Delay(50);
                    }
                }

                Assert.NotNull(terminal);
                Assert.Equal(GamePhase.RoundEnd, terminal.PublicView!.Board.Phase);
                Assert.Equal(0u, terminal.PublicView.Board.WallRemaining);
                Assert.Equal(terminal.RoundId, terminal.PublicView.SettlementRoundId);
                Assert.Contains(
                    terminal.PublicView.SettlementEvents,
                    e => e.EventType == EventType.DrawGame);
                Assert.Contains(
                    terminal.PublicView.SettlementEvents,
                    e => e.EventType == EventType.RoundEnded);
                Assert.False(terminal.PublicView.Session.Finished);
                Assert.True(actions >= 60, "a real wall exhaustion needs a real natural sequence, got " + actions);

                // Multiple ticks after the terminal must not advance or re-publish. A fresh
                // spectator admission is a new full baseline of the still-current authority.
                ulong terminalVersion = terminal.StateVersion;
                int terminalEvents = terminal.PublicView.SettlementEvents.Count;
                await Task.Delay(500);
                await using TableTestClient observer =
                    await host.ConnectSpectatorAsync("room-b2-wall", "acct-b2-wall-observer");
                Envelope laterEnvelope = await observer.ReadUntilAsync(
                    e => e.PublicUpdate?.Snapshot != null &&
                         e.PublicUpdate.Snapshot.StateVersion >= terminalVersion,
                    TimeSpan.FromSeconds(5));
                PublicSnapshot later = laterEnvelope.PublicUpdate.Snapshot;
                Assert.Equal(terminalVersion, later.StateVersion);
                Assert.Equal(terminalEvents, later.View.SettlementEvents.Count);
                Assert.Equal(GamePhase.RoundEnd, later.View.Board.Phase);
            }
            finally
            {
                foreach (TableTestClient client in clients)
                {
                    await client.DisposeAsync();
                }
            }
        }

        [Fact]
        public async Task ForgedTsumoPayload_WithoutWinningHand_IsRejectedWithZeroSettlementSideEffect()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(roomId =>
                {
                    var fixture = new N1aTable { Dealer = 0 };
                    fixture.Hand(0, new[]
                    {
                        new TileId(0), new TileId(12), new TileId(24),
                        new TileId(36), new TileId(48), new TileId(60),
                        new TileId(72), new TileId(84), new TileId(96),
                        new TileId(108), new TileId(112), new TileId(116),
                        new TileId(120)
                    });
                    fixture.Drawn(0, new TileId(124));
                    return fixture.Build(roomId + "-r1");
                }),
                clock: FrozenUtcClock.Instance);
            TableTestClient client = await host.ConnectPlayerAsync("room-b2-forged", "acct-b2-forged", 0);
            try
            {
                PlayerSnapshot before = await WaitForDecisionAsync(client, "room-b2-forged-r1");
                ulong versionBefore = before.StateVersion;
                string requestId = TableWire.RandomId("req-b2-forged");
                await client.SendAsync(TableWire.Command(
                    client.Welcome!,
                    requestId,
                    Contracts.CommandKind.SubmitAction,
                    TableWire.SubmitActionPayload(
                        before.RoundId,
                        before.PrivateView!.Decision.DecisionId,
                        before.PrivateView.Decision.ControlEpoch,
                        (uint)ProtocolActionType.DeclareTsumo,
                        new uint[] { 125 }),
                    expectedStateVersion: versionBefore));
                Envelope receipt = await client.ReadUntilAsync(
                    e => e.MessageType == MessageType.Receipt && e.RequestId == requestId,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Rejected, receipt.Receipt.Status);
                Assert.False(string.IsNullOrEmpty(receipt.Receipt.ReasonCode));

                PlayerSnapshot after = await ReadSnapshotAsync(client, TimeSpan.FromSeconds(5));
                Assert.Equal(versionBefore, after.StateVersion);
                Assert.NotEqual(GamePhase.RoundEnd, after.PublicView!.Board.Phase);
                Assert.Empty(after.PublicView.SettlementEvents);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        [Fact]
        public async Task SpectatorTsumo_IsRefusedBeforeActor_AndWrongSeatIdentityIsProtocolViolation()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(DealerTsumoRound),
                clock: FrozenUtcClock.Instance);
            TableTestClient player = await host.ConnectPlayerAsync("room-b2-gate", "acct-b2-gate", 0);
            TableTestClient spectator = await host.ConnectSpectatorAsync("room-b2-gate", "acct-b2-gate-spec");
            try
            {
                PlayerSnapshot snapshot = await WaitForDecisionAsync(player, "room-b2-gate-r1");
                Contracts.Wire.DecisionView decision = snapshot.PrivateView!.Decision;

                string spectatorRequest = TableWire.RandomId("req-b2-spec");
                await spectator.SendAsync(TableWire.Command(
                    spectator.Welcome!,
                    spectatorRequest,
                    Contracts.CommandKind.SubmitAction,
                    TableWire.SubmitActionPayload(
                        snapshot.RoundId,
                        decision.DecisionId,
                        decision.ControlEpoch,
                        (uint)ProtocolActionType.DeclareTsumo,
                        new uint[] { 12 }),
                    expectedStateVersion: snapshot.StateVersion));
                Envelope spectatorError = await spectator.ReadUntilAsync(
                    e => e.MessageType == MessageType.Error,
                    TimeSpan.FromSeconds(10));
                Assert.Equal(ProtocolErrorCode.Unauthenticated, spectatorError.Error.Code);

                string wrongSeatRequest = TableWire.RandomId("req-b2-seat");
                await player.SendAsync(TableWire.Command(
                    player.Welcome!,
                    wrongSeatRequest,
                    Contracts.CommandKind.SubmitAction,
                    TableWire.SubmitActionPayload(
                        snapshot.RoundId,
                        decision.DecisionId,
                        decision.ControlEpoch,
                        (uint)ProtocolActionType.DeclareTsumo,
                        new uint[] { 12 }),
                    seatId: 1,
                    expectedStateVersion: snapshot.StateVersion));
                Envelope seatError = await player.ReadUntilAsync(
                    e => e.MessageType == MessageType.Error,
                    TimeSpan.FromSeconds(10));
                Assert.Equal(ProtocolErrorCode.ProtocolViolation, seatError.Error.Code);

                PlayerSnapshot after = await ReadSnapshotAsync(player, TimeSpan.FromSeconds(5));
                Assert.Equal(snapshot.StateVersion, after.StateVersion);
                Assert.NotEqual(GamePhase.RoundEnd, after.PublicView!.Board.Phase);
            }
            finally
            {
                await spectator.DisposeAsync();
                await player.DisposeAsync();
            }
        }

        [Fact]
        public async Task IllegalRon_NotAdvertised_IsRejectedWithZeroSettlementSideEffect()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(roomId =>
                {
                    var fixture = new N1aTable { Dealer = 1 };
                    // Seat 0 can pon the discarded 1m but its thirteen tiles plus 1m are
                    // not a complete hand, so no Ron may be advertised or executed.
                    fixture.Hand(0,
                        new TileId(0), new TileId(1),
                        new TileId(4), new TileId(8), new TileId(16), new TileId(20),
                        new TileId(28), new TileId(40), new TileId(48), new TileId(56),
                        new TileId(76), new TileId(84), new TileId(92));
                    fixture.Hand(1, new TileId(2));
                    fixture.Drawn(1, new TileId(127));
                    return fixture.Build(roomId + "-r1");
                }),
                clock: FrozenUtcClock.Instance);
            TableTestClient seat1 = await host.ConnectPlayerAsync(
                "room-b2-illegal-ron", "acct-b2-illegal-ron-1", 1);
            TableTestClient seat0 = await host.ConnectPlayerAsync(
                "room-b2-illegal-ron", "acct-b2-illegal-ron-0", 0);
            try
            {
                PlayerSnapshot dealer = await WaitForDecisionAsync(seat1, "room-b2-illegal-ron-r1");
                Contracts.Wire.DecisionView discardDecision = dealer.PrivateView!.Decision;
                Contracts.Wire.LegalActionView discard = discardDecision.Actions.First(
                    a => a.ActionType == (uint)ProtocolActionType.DiscardTile &&
                         a.TileIds.Contains(2u));
                await seat1.SendAsync(TableWire.Command(
                    seat1.Welcome!,
                    TableWire.RandomId("req-b2-illegal-ron-discard"),
                    Contracts.CommandKind.SubmitAction,
                    TableWire.SubmitActionPayload(
                        dealer.RoundId,
                        discardDecision.DecisionId,
                        discardDecision.ControlEpoch,
                        (uint)ProtocolActionType.DiscardTile,
                        new uint[] { 2 }),
                    expectedStateVersion: dealer.StateVersion));
                Envelope discardReceipt = await seat1.ReadUntilAsync(
                    e => e.MessageType == MessageType.Receipt,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, discardReceipt.Receipt.Status);

                PlayerSnapshot response = await WaitForDecisionAsync(seat0, "room-b2-illegal-ron-r1");
                Contracts.Wire.DecisionView responseDecision = response.PrivateView!.Decision;
                Assert.DoesNotContain(
                    responseDecision.Actions,
                    a => a.ActionType == (uint)ProtocolActionType.DeclareRon);
                ulong version = response.StateVersion;
                string requestId = TableWire.RandomId("req-b2-illegal-ron");
                await seat0.SendAsync(TableWire.Command(
                    seat0.Welcome!,
                    requestId,
                    Contracts.CommandKind.SubmitAction,
                    TableWire.SubmitActionPayload(
                        response.RoundId,
                        responseDecision.DecisionId,
                        responseDecision.ControlEpoch,
                        (uint)ProtocolActionType.DeclareRon,
                        new uint[] { 2 }),
                    expectedStateVersion: version));
                Envelope receipt = await seat0.ReadUntilAsync(
                    e => e.MessageType == MessageType.Receipt && e.RequestId == requestId,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Rejected, receipt.Receipt.Status);
                Assert.False(string.IsNullOrEmpty(receipt.Receipt.ReasonCode));

                PlayerSnapshot after = await ReadSnapshotAsync(seat0, TimeSpan.FromSeconds(5));
                Assert.Equal(version, after.StateVersion);
                Assert.NotEqual(GamePhase.RoundEnd, after.PublicView!.Board.Phase);
                Assert.Empty(after.PublicView.SettlementEvents);
                Assert.Equal(0, host.GetRoomHost("room-b2-illegal-ron")
                    .Store is InMemoryTableStore s ? s.OutboxCount : -1);
            }
            finally
            {
                await seat0.DisposeAsync();
                await seat1.DisposeAsync();
            }
        }

        [Fact]
        public async Task TerminalCommit_WithAdvancingUtc_UsesCoherentCheckpointAndReplaysOnce()
        {
            // This clock reproduced the old terminal-checkpoint UTC mismatch. After
            // the Runtime correction, an advancing clock must produce a coherent
            // terminal checkpoint and one outbox row, including original-request replay.
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(DealerTsumoRound),
                clock: new AdvancingUtcClock());
            TableTestClient client = await host.ConnectPlayerAsync(
                "room-b2-clock-race", "acct-b2-clock-race", 0);
            try
            {
                PlayerSnapshot before = await WaitForDecisionAsync(client, "room-b2-clock-race-r1");
                RoomHost room = host.GetRoomHost("room-b2-clock-race");
                Contracts.Wire.DecisionView decision = before.PrivateView!.Decision;
                var request = new TableSettlementRequest(
                    new TableSettlementIntent(before.RoundId, TableSettlementAction.Tsumo, 0),
                    new RequestId("req-b2-clock-race"),
                    new StateVersion(before.StateVersion),
                    room.OwnerEpoch,
                    new ControlEpoch(decision.ControlEpoch),
                    new DecisionId(decision.DecisionId),
                    scopeId: room.ServiceScope.ScopeId,
                    api: "SUBMIT_ACTION",
                    payloadSha256: TablePayloadSha256.Compute(
                        Encoding.UTF8.GetBytes("clock-race-settlement")));

                TableOperationResult result = await room.Actor.SubmitSettlementAsync(request);
                Assert.Equal(TableOperationStatus.Accepted, result.Status);
                Assert.True(room.Actor.GetRoundStateForHost().IsFinished);
                PlayerSnapshot terminal = await WaitForPhaseAsync(client, GamePhase.RoundEnd);
                Assert.Equal(before.StateVersion + 1UL, terminal.StateVersion);
                InMemoryTableStore store = await AssertSingleTerminalOutboxAsync(
                    host, "room-b2-clock-race", terminal);
                TableCheckpoint? checkpoint = await store.LoadCheckpointAsync(
                    "room-b2-clock-race", CancellationToken.None);
                Assert.NotNull(checkpoint);
                TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(checkpoint!.TimingPayload);
                Assert.Equal(timing.CapturedAtUtcMs, checkpoint.CreatedAtUtcMs);
                Assert.Equal(timing.CapturedMonotonicMillis, checkpoint.CapturedMonotonicMillis);
                Assert.Equal(timing.BootId, checkpoint.BootId);

                TableOperationResult replay = await room.Actor.SubmitSettlementAsync(request);
                Assert.Equal(TableOperationStatus.Replayed, replay.Status);
                Assert.Equal((long)terminal.StateVersion, room.Actor.GetRoundStateForHost().Version);
                Assert.Equal(1, store.OutboxCount);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        [Fact]
        public async Task ClosedKanRinshan_IsAdvertisedAndSettlesThroughTheRealActor()
        {
            RoundStateMachine kan = ClosedKanRinshanWin("room-b2-kan-r1");
            Assert.Single(kan.Melds);
            Assert.Equal(RoundMeldKind.ClosedKan, kan.Melds[0].Kind);
            Assert.Equal(11, kan.Tiles.TilesInZone(TileZone.Hand, 0).Count);
            Assert.True(
                TableServiceRonEligibility.CanTsumo(kan, 0),
                "a closed-kan rinshan win must be advertised from the frozen evaluator, not a 14-tile count");

            await using TableActor actor = RoomHost.CreateActor(
                "room-b2-kan",
                new TableServiceOptions(),
                new TableServiceCommitPort(new OwnerEpoch(1)),
                FrozenUtcClock.Instance,
                publicationSink: null,
                new TableStreamOptions(),
                durable: null,
                new OwnerEpoch(1),
                kan,
                new TableRoomDecisionState());
            TableOperationResult result = await actor.SubmitSettlementAsync(
                new TableSettlementRequest(
                    new TableSettlementIntent(kan.RoundId, TableSettlementAction.Tsumo, 0),
                    new RequestId("req-b2-kan"),
                    new StateVersion(0),
                    new OwnerEpoch(1),
                    new ControlEpoch(0)));
            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            Assert.True(actor.GetRoundStateForHost().IsFinished);
            Assert.Equal(100000L, actor.GetRoundStateForHost().Result!.FinalScores.Sum());
        }

        [Fact]
        public async Task OpenKanRinshan_WithOpenMeld_IsAdvertisedAndSettlesThroughTheRealActor()
        {
            RoundStateMachine kan = OpenKanRinshanWin("room-b2-openkan-r1");
            Assert.Single(kan.Melds);
            Assert.Equal(RoundMeldKind.OpenKan, kan.Melds[0].Kind);
            Assert.Equal(11, kan.Tiles.TilesInZone(TileZone.Hand, 0).Count);
            Assert.True(
                TableServiceRonEligibility.CanTsumo(kan, 0),
                "an open-kan yakuhai rinshan win must not be filtered by a closed 14-tile count");

            await using TableActor actor = RoomHost.CreateActor(
                "room-b2-openkan",
                new TableServiceOptions(),
                new TableServiceCommitPort(new OwnerEpoch(1)),
                FrozenUtcClock.Instance,
                publicationSink: null,
                new TableStreamOptions(),
                durable: null,
                new OwnerEpoch(1),
                kan,
                new TableRoomDecisionState());
            TableOperationResult result = await actor.SubmitSettlementAsync(
                new TableSettlementRequest(
                    new TableSettlementIntent(kan.RoundId, TableSettlementAction.Tsumo, 0),
                    new RequestId("req-b2-openkan"),
                    new StateVersion(0),
                    new OwnerEpoch(1),
                    new ControlEpoch(0)));
            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            Assert.True(actor.GetRoundStateForHost().IsFinished);
        }

        [Fact]
        public void TsumoEligibility_RejectsIncompleteHandAndWrongSeat()
        {
            RoundStateMachine win = DealerTsumoRound("room-b2-eligibility");
            Assert.False(
                TableServiceRonEligibility.CanTsumo(win, 1),
                "only the current seat may be advertised for tsumo");
            var fixture = new N1aTable { Dealer = 0 };
            fixture.Hand(0, Tiles(0, 12, 24, 36, 48, 60, 72, 84, 96, 108, 112, 116, 120));
            fixture.Drawn(0, new TileId(124));
            RoundStateMachine incomplete = fixture.Build("room-b2-incomplete-r1");
            Assert.False(
                TableServiceRonEligibility.CanTsumo(incomplete, 0),
                "an incomplete hand is not a tsumo candidate");
        }

        [Fact]
        public void TemporaryFuriten_FailsClosedForRonEvenWithACompleteHand()
        {
            var fixture = new N1aTable { Dealer = 1 };
            fixture.Hand(0, Tiles(124, 125, 126, 4, 8, 48, 52, 56, 92, 96, 100, 16, 17));
            fixture.Hand(1, new TileId(0));
            fixture.Drawn(1, new TileId(127));
            RoundStateMachine round = fixture.Build("room-b2-temp-furiten-r1");
            round = Apply(
                round,
                RoundCommand.Discard(round.RoundId, 1, new TileId(0), new[] { 0 }));
            Assert.True(
                TableServiceRonEligibility.CanRon(
                    round, TableFuritenSnapshot.Empty(round.RoundId), 0));
            Assert.False(
                TableServiceRonEligibility.CanRon(
                    round,
                    new TableFuritenSnapshot(round.RoundId, temporaryMask: 1, riichiMask: 0),
                    0),
                "temporary furiten must fail closed for Ron");
        }

        [Fact]
        public async Task DiscardFuriten_NonTargetWaitInHistory_IsNotAdvertisedAndActorRejectsRon()
        {
            RoundStateMachine ron = DiscardFuritenWindow("room-b2-discard-furiten-r1");
            IReadOnlyCollection<int> waits = HandDecomposer.CalculateWaits(
                ron.Tiles.TilesInZone(TileZone.Hand, 0));
            Assert.Contains(new TileId(12).TypeIndex, waits);
            Assert.Contains(new TileId(0).TypeIndex, waits);
            Assert.True(
                TableServiceRonEligibility.HasDiscardFuriten(ron, 0),
                "the seat discarded the other wait (4m), not only the current target (1m)");
            Assert.False(
                TableServiceRonEligibility.CanRon(
                    ron, TableFuritenSnapshot.Empty(ron.RoundId), 0));
            Assert.False(
                TableServiceRonEligibility.CanRon(ron, null, 0),
                "missing temporary/riichi facts must fail closed even without discard history");

            await using TableActor actor = RoomHost.CreateActor(
                "room-b2-discard-furiten",
                new TableServiceOptions(),
                new TableServiceCommitPort(new OwnerEpoch(1)),
                FrozenUtcClock.Instance,
                publicationSink: null,
                new TableStreamOptions(),
                durable: null,
                new OwnerEpoch(1),
                ron,
                new TableRoomDecisionState());
            TableOperationResult result = await actor.SubmitSettlementAsync(
                new TableSettlementRequest(
                    new TableSettlementIntent(ron.RoundId, TableSettlementAction.Ron, 0),
                    new RequestId("req-b2-discard-furiten"),
                    new StateVersion(0),
                    new OwnerEpoch(1),
                    new ControlEpoch(0)));
            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.False(actor.GetRoundStateForHost().IsFinished);
            Assert.Contains("FURITENRON", result.ReasonCode, StringComparison.Ordinal);
        }

        [Fact]
        public void DiscardFuriten_CompleteHistoryIncludesCalledAwayDiscard()
        {
            var fixture = new N1aTable { Dealer = 0 };
            fixture.Hand(0, Tiles(0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48));
            fixture.Drawn(0, new TileId(127));
            fixture.Hand(1, Tiles(1, 2));
            RoundStateMachine round = fixture.Build("room-b2-called-away-r1");
            round = Apply(
                round,
                RoundCommand.Discard(round.RoundId, 0, new TileId(0), new[] { 1 }));
            Assert.Equal(RoundPhase.ResponseWindow, round.Phase);
            round = Apply(
                round,
                RoundCommand.Pon(round.RoundId, 1, new TileId(0), new TileId(1), new TileId(2)));
            RoundMeld called = Assert.Single(round.Melds);
            Assert.Equal(0, called.FromSeat);
            Assert.Equal(0, called.CalledTile.TypeIndex);
            Assert.Contains(
                0,
                TableServiceRonEligibility.CompleteDiscardHistoryTypes(round, 0));
            Assert.DoesNotContain(
                0,
                TableServiceRonEligibility.CompleteDiscardHistoryTypes(round, 1));
        }

        [Fact]
        public async Task AbortiveDraw_OriginalRequestReplaysFromReceiptAfterRoundEnd()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(NineTerminals),
                clock: FrozenUtcClock.Instance);
            TableTestClient client = await host.ConnectPlayerAsync(
                "room-b2-abortive-replay", "acct-b2-abortive-replay", 0);
            try
            {
                PlayerSnapshot before = await WaitForDecisionAsync(client, "room-b2-abortive-replay-r1");
                string requestId = TableWire.RandomId("req-b2-abortive-replay");
                Envelope command = TableWire.Command(
                    client.Welcome!,
                    requestId,
                    Contracts.CommandKind.SubmitAction,
                    TableWire.SubmitActionPayload(
                        before.RoundId,
                        before.PrivateView!.Decision.DecisionId,
                        before.PrivateView.Decision.ControlEpoch,
                        (uint)ProtocolActionType.DeclareAbortiveDraw,
                        null),
                    expectedStateVersion: before.StateVersion);
                await client.SendAsync(command);
                Envelope first = await client.ReadUntilAsync(
                    e => e.MessageType == MessageType.Receipt && e.RequestId == requestId,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, first.Receipt.Status);

                PlayerSnapshot terminal = await WaitForPhaseAsync(client, GamePhase.RoundEnd);
                ulong terminalVersion = terminal.StateVersion;
                int terminalEvents = terminal.PublicView!.SettlementEvents.Count;
                InMemoryTableStore store = Assert.IsType<InMemoryTableStore>(
                    host.GetRoomHost("room-b2-abortive-replay").Store);
                Assert.Equal(1, store.OutboxCount);

                // Byte-identical retry on the original identity/scope/requestId/digest.
                await client.SendAsync(command);
                Envelope replay = await client.ReadUntilAsync(
                    e => e.MessageType == MessageType.Receipt && e.RequestId == requestId,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, replay.Receipt.Status);
                Assert.True(replay.Receipt.Replayed, "the original abortive draw receipt must answer the retry");
                Assert.Equal(first.Receipt.CommandVersion, replay.Receipt.CommandVersion);
                Assert.Equal(terminalVersion, first.Receipt.CommandVersion);
                Assert.Equal(1, store.OutboxCount);

                // Same request id but different payload bytes must conflict, never execute a
                // second terminal under the old receipt.
                Envelope tampered = TableWire.Command(
                    client.Welcome!,
                    requestId,
                    Contracts.CommandKind.SubmitAction,
                    TableWire.SubmitActionPayload(
                        before.RoundId,
                        "tampered-decision",
                        before.PrivateView.Decision.ControlEpoch,
                        (uint)ProtocolActionType.DeclareAbortiveDraw,
                        null),
                    expectedStateVersion: before.StateVersion);
                await client.SendAsync(tampered);
                Envelope conflict = await client.ReadUntilAsync(
                    e => e.MessageType == MessageType.Error,
                    TimeSpan.FromSeconds(10));
                Assert.Equal(ProtocolErrorCode.RequestIdConflict, conflict.Error.Code);
                Assert.Equal(1, store.OutboxCount);

                Assert.Equal(1, store.OutboxCount);

                await using TableTestClient observer = await host.ConnectSpectatorAsync(
                    "room-b2-abortive-replay", "acct-b2-abortive-replay-observer");
                Envelope baseline = await observer.ReadUntilAsync(
                    e => e.PublicUpdate?.Snapshot != null,
                    TimeSpan.FromSeconds(5));
                Assert.Equal(terminalVersion, baseline.PublicUpdate.Snapshot.StateVersion);
                Assert.Equal(terminalEvents, baseline.PublicUpdate.Snapshot.View.SettlementEvents.Count);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        [Fact]
        public async Task TransientExhaustiveCommitFailure_RetriesSameIdentityAndSettlesOnce()
        {
            ExhaustiveFixture fixture = BuildExhaustiveFixture("room-b2-retry");
            var failsOnce = new FailOnceCommitPort();
            await using TableActor actor = RoomHost.CreateActor(
                fixture.RoomId,
                new TableServiceOptions(),
                failsOnce,
                FrozenUtcClock.Instance,
                publicationSink: null,
                new TableStreamOptions(),
                durable: null,
                new OwnerEpoch(1),
                fixture.Round,
                fixture.DecisionState,
                fixture.Source);
            var scope = new TableScopeRecord(
                new RequestScopeId("scope-b2-retry"),
                "svc-b2-retry",
                TableScopeRole.Player,
                fixture.RoomId,
                1UL,
                ulong.MaxValue);
            var host = new RoomHost(
                fixture.RoomId,
                new TableServiceOptions(),
                actor,
                null!,
                null!,
                null!,
                null!,
                new OwnerEpoch(1),
                fixture.DecisionState,
                scope);
            fixture.Source.Capture(new TableProjectionContextRequest(
                fixture.RoomId,
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                new OwnerEpoch(1),
                0L,
                0L,
                FrozenUtcClock.Instance.MonotonicMillis,
                FrozenUtcClock.Instance.UtcNowMs));

            await fixture.DecisionState.DriveAsync(host);
            Assert.False(actor.GetRoundStateForHost().IsFinished);
            Assert.Equal(1, failsOnce.Calls);
            for (int i = 0; i < 3; i++)
            {
                await fixture.DecisionState.DriveAsync(host);
            }

            Assert.True(actor.GetRoundStateForHost().IsFinished);
            Assert.Equal(2, failsOnce.Calls);
            Assert.Equal(100000L, actor.GetRoundStateForHost().Result!.FinalScores.Sum());
        }

        [Fact]
        public async Task ConcurrentExhaustiveTicks_SubmitOnlyOneInFlightRequest()
        {
            ExhaustiveFixture fixture = BuildExhaustiveFixture("room-b2-inflight");
            var blocking = new BlockingCommitPort();
            await using TableActor actor = RoomHost.CreateActor(
                fixture.RoomId,
                new TableServiceOptions(),
                blocking,
                FrozenUtcClock.Instance,
                publicationSink: null,
                new TableStreamOptions(),
                durable: null,
                new OwnerEpoch(1),
                fixture.Round,
                fixture.DecisionState,
                fixture.Source);
            var scope = new TableScopeRecord(
                new RequestScopeId("scope-b2-inflight"),
                "svc-b2-inflight",
                TableScopeRole.Player,
                fixture.RoomId,
                1UL,
                ulong.MaxValue);
            var host = new RoomHost(
                fixture.RoomId,
                new TableServiceOptions(),
                actor,
                null!,
                null!,
                null!,
                null!,
                new OwnerEpoch(1),
                fixture.DecisionState,
                scope);
            fixture.Source.Capture(new TableProjectionContextRequest(
                fixture.RoomId,
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                new OwnerEpoch(1),
                0L,
                0L,
                FrozenUtcClock.Instance.MonotonicMillis,
                FrozenUtcClock.Instance.UtcNowMs));

            Task first = fixture.DecisionState.DriveAsync(host);
            await blocking.Started.WaitAsync(TimeSpan.FromSeconds(5));
            // The concurrent tick may wait on the actor's timing snapshot, but it must never
            // enter a second commit while the first is in flight.
            Task second = fixture.DecisionState.DriveAsync(host);
            await Task.Delay(100);
            Assert.Equal(1, blocking.Calls);
            blocking.Release();
            await Task.WhenAll(first, second);
            Assert.True(actor.GetRoundStateForHost().IsFinished);
            Assert.Equal(1, blocking.Calls);
        }

        [Fact]
        public async Task PersistentExhaustiveCommitFailure_StopsAfterBoundedAttemptsWithoutInfiniteRetry()
        {
            ExhaustiveFixture fixture = BuildExhaustiveFixture("room-b2-persistent");
            var failing = new FailAlwaysCommitPort();
            await using TableActor actor = RoomHost.CreateActor(
                fixture.RoomId,
                new TableServiceOptions(),
                failing,
                FrozenUtcClock.Instance,
                publicationSink: null,
                new TableStreamOptions(),
                durable: null,
                new OwnerEpoch(1),
                fixture.Round,
                fixture.DecisionState,
                fixture.Source);
            var scope = new TableScopeRecord(
                new RequestScopeId("scope-b2-persistent"),
                "svc-b2-persistent",
                TableScopeRole.Player,
                fixture.RoomId,
                1UL,
                ulong.MaxValue);
            var host = new RoomHost(
                fixture.RoomId,
                new TableServiceOptions(),
                actor,
                null!,
                null!,
                null!,
                null!,
                new OwnerEpoch(1),
                fixture.DecisionState,
                scope);
            fixture.Source.Capture(new TableProjectionContextRequest(
                fixture.RoomId,
                fixture.Round,
                new StateVersion(0),
                new EventSeq(0),
                new OwnerEpoch(1),
                0L,
                0L,
                FrozenUtcClock.Instance.MonotonicMillis,
                FrozenUtcClock.Instance.UtcNowMs));

            for (int i = 0; i < 8; i++)
            {
                await fixture.DecisionState.DriveAsync(host);
            }

            Assert.False(actor.GetRoundStateForHost().IsFinished);
            Assert.InRange(failing.Calls, 1, 3);
        }

        [Fact]
        public async Task RawSettlementBearingEnvelope_IsRejectedBeforeAnyScoring()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(DealerTsumoRound),
                clock: FrozenUtcClock.Instance);
            TableTestClient client = await host.ConnectPlayerAsync(
                "room-b2-raw", "acct-b2-raw", 0);
            try
            {
                PlayerSnapshot before = await WaitForDecisionAsync(client, "room-b2-raw-r1");
                RoomHost room = host.GetRoomHost("room-b2-raw");
                RoundStateMachine current = room.Actor.GetRoundStateForHost();
                var forgedSettlement = new RoundSettlement(
                    "forged:" + current.RoundId,
                    RoundOutcome.Tsumo(0),
                    new long[] { 25000L, 25000L, 25000L, 25000L },
                    0,
                    new PersistedUtcTime(DateTimeOffset.UnixEpoch),
                    0L);
                byte[] rawPayload = Encoding.UTF8.GetBytes("forged-settlement-body");
                var envelope = new TableCommandEnvelope(
                    RoundCommand.Tsumo(current.RoundId, 0, forgedSettlement),
                    new RequestId("req-b2-raw-settlement"),
                    new StateVersion((ulong)current.Version),
                    room.OwnerEpoch,
                    new ControlEpoch(before.PrivateView!.Decision.ControlEpoch),
                    scopeId: room.ServiceScope.ScopeId,
                    api: "SUBMIT_ACTION",
                    payloadSha256: TablePayloadSha256.Compute(rawPayload));
                TableOperationResult result = await room.Actor.SubmitAsync(envelope);
                Assert.Equal(TableOperationStatus.Rejected, result.Status);
                Assert.Equal(TableSettlementReasonCode.RawEntryRejected, result.ReasonCode);
                Assert.Equal((long)before.StateVersion, room.Actor.GetRoundStateForHost().Version);
                Assert.NotEqual(GamePhase.RoundEnd, before.PublicView!.Board.Phase);

                await Task.Delay(200);
                Assert.Equal(0, ((InMemoryTableStore)room.Store).OutboxCount);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        [Fact]
        public async Task SettlementNegativeMatrix_WrongRoundVersionControlDecision_AllRejectedWithZeroSideEffect_ThenSameKeyReplay()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(DealerTsumoRound),
                clock: FrozenUtcClock.Instance);
            TableTestClient client = await host.ConnectPlayerAsync("room-b2-neg", "acct-b2-neg", 0);
            try
            {
                PlayerSnapshot before = await WaitForDecisionAsync(client, "room-b2-neg-r1");
                ulong version = before.StateVersion;
                Contracts.Wire.DecisionView decision = before.PrivateView!.Decision;

                async Task AssertRejectedAsync(
                    string label,
                    byte[] payload,
                    ulong expectedVersion,
                    ulong? ownerEpoch = null)
                {
                    string requestId = TableWire.RandomId("req-b2-neg-" + label);
                    await client.SendAsync(TableWire.Command(
                        client.Welcome!,
                        requestId,
                        Contracts.CommandKind.SubmitAction,
                        payload,
                        ownerEpoch: ownerEpoch,
                        expectedStateVersion: expectedVersion));
                    Envelope response = await client.ReadUntilAsync(
                        e => (e.MessageType == MessageType.Receipt && e.RequestId == requestId) ||
                             e.MessageType == MessageType.Error,
                        TimeSpan.FromSeconds(10));
                    Assert.True(
                        response.MessageType == MessageType.Receipt,
                        label + " returned " + response.MessageType);
                    Assert.Equal(Contracts.Wire.ReceiptStatus.Rejected, response.Receipt.Status);
                }

                byte[] wrongRound = TableWire.SubmitActionPayload(
                    "room-b2-neg-other-r1", decision.DecisionId, decision.ControlEpoch,
                    (uint)ProtocolActionType.DeclareTsumo, new uint[] { 12 });
                await AssertRejectedAsync("round", wrongRound, version);

                byte[] futureVersion = TableWire.SubmitActionPayload(
                    before.RoundId, decision.DecisionId, decision.ControlEpoch,
                    (uint)ProtocolActionType.DeclareTsumo, new uint[] { 12 });
                await AssertRejectedAsync("state", futureVersion, version + 1UL);

                byte[] wrongControl = TableWire.SubmitActionPayload(
                    before.RoundId, decision.DecisionId, decision.ControlEpoch + 1UL,
                    (uint)ProtocolActionType.DeclareTsumo, new uint[] { 12 });
                await AssertRejectedAsync("epoch", wrongControl, version);

                byte[] wrongOwner = TableWire.SubmitActionPayload(
                    before.RoundId, decision.DecisionId, decision.ControlEpoch,
                    (uint)ProtocolActionType.DeclareTsumo, new uint[] { 12 });
                await AssertRejectedAsync("owner", wrongOwner, version, client.Welcome!.OwnerEpoch + 1UL);

                byte[] wrongDecision = TableWire.SubmitActionPayload(
                    before.RoundId, "not-the-open-decision", decision.ControlEpoch,
                    (uint)ProtocolActionType.DeclareTsumo, new uint[] { 12 });
                await AssertRejectedAsync("decision", wrongDecision, version);

                await using (TableTestClient observer =
                    await host.ConnectSpectatorAsync("room-b2-neg", "acct-b2-neg-observer"))
                {
                    Envelope baselineEnvelope = await observer.ReadUntilAsync(
                        e => e.PublicUpdate?.Snapshot != null,
                        TimeSpan.FromSeconds(10));
                    Assert.Equal(version, baselineEnvelope.PublicUpdate.Snapshot.StateVersion);
                    Assert.NotEqual(GamePhase.RoundEnd, baselineEnvelope.PublicUpdate.Snapshot.View.Board.Phase);
                }

                byte[] correct = TableWire.SubmitActionPayload(
                    before.RoundId, decision.DecisionId, decision.ControlEpoch,
                    (uint)ProtocolActionType.DeclareTsumo, new uint[] { 12 });
                string finalRequest = TableWire.RandomId("req-b2-neg-final");
                await client.SendAsync(TableWire.Command(
                    client.Welcome!, finalRequest, Contracts.CommandKind.SubmitAction, correct,
                    expectedStateVersion: version));
                Envelope applied = await client.ReadUntilAsync(
                    e => e.MessageType == MessageType.Receipt && e.RequestId == finalRequest,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, applied.Receipt.Status);
                PlayerSnapshot terminal = await WaitForPhaseAsync(client, GamePhase.RoundEnd);
                Assert.Equal(version + 1UL, terminal.StateVersion);

                // The same request identity and bytes are answered from the receipt ledger;
                // the terminal is not re-applied and no second settlement appears.
                await client.SendAsync(TableWire.Command(
                    client.Welcome!, finalRequest, Contracts.CommandKind.SubmitAction, correct,
                    expectedStateVersion: version));
                Envelope replay = await client.ReadUntilAsync(
                    e => e.MessageType == MessageType.Receipt && e.RequestId == finalRequest,
                    TimeSpan.FromSeconds(20));
                Assert.True(replay.Receipt.Replayed);
                Assert.Equal(applied.Receipt.CommandVersion, replay.Receipt.CommandVersion);
                await Task.Delay(300);
                await using TableTestClient afterReplayObserver =
                    await host.ConnectSpectatorAsync("room-b2-neg", "acct-b2-neg-after");
                Envelope afterReplayEnvelope = await afterReplayObserver.ReadUntilAsync(
                    e => e.PublicUpdate?.Snapshot != null,
                    TimeSpan.FromSeconds(10));
                PublicSnapshot afterReplay = afterReplayEnvelope.PublicUpdate.Snapshot;
                Assert.Equal(version + 1UL, afterReplay.StateVersion);
                Assert.Single(
                    afterReplay.View.SettlementEvents,
                    e => e.EventType == EventType.TsumoWin);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        [Fact]
        public void SettlementFactsPort_UnboundOrForeignSource_FailsClosed()
        {
            var port = new TableServiceSettlementFactsPort();
            var source = new TableSettlementSourceRef("r1", 0, 1UL);
            var intent = new TableSettlementIntent("r1", TableSettlementAction.Tsumo, 0);
            Assert.Throws<InvalidOperationException>(() => port.Resolve(source, intent));

            RoundStateMachine round = DealerTsumoRound("room-facts");
            var actual = new TableSettlementSourceRef(round.RoundId, round.Version, round.SourceFingerprint);
            var foreign = new TableSettlementSourceRef(round.RoundId + "-other", round.Version, round.SourceFingerprint);
            var furiten = TableFuritenSnapshot.Empty(round.RoundId);
            TableSettlementHostFacts facts = TableServiceSettlementFacts.Build(actual, round, furiten);
            Assert.Equal(4, facts.Seats.Count);
            Assert.All(facts.Seats, seat => Assert.False(seat.IsIppatsu));
            Assert.Throws<InvalidOperationException>(
                () => TableServiceSettlementFacts.Build(foreign, round, furiten));
            Assert.Throws<InvalidOperationException>(
                () => TableServiceSettlementFacts.Build(
                    actual, round, TableFuritenSnapshot.Empty("another-round")));

            TableSettlementHostFacts withFuriten = TableServiceSettlementFacts.Build(
                actual,
                round,
                new TableFuritenSnapshot(round.RoundId, temporaryMask: 1, riichiMask: 2));
            Assert.True(withFuriten.Seats[0].IsTemporaryFuriten);
            Assert.True(withFuriten.Seats[1].IsRiichiFuriten);
            Assert.False(withFuriten.Seats[0].IsRiichiFuriten);
            Assert.False(withFuriten.Seats[2].IsTemporaryFuriten);

            // The service checkpoint projection persists the same two bits; the runtime
            // actor restores them from this payload (cold-recovery matrix).
            var projectionSource = new TableProjectionContextSource(initialRound: round);
            TableProjectionContextData projected = projectionSource.Capture(
                new TableProjectionContextRequest(
                    "room-facts",
                    round,
                    new StateVersion(0),
                    new EventSeq(0),
                    new OwnerEpoch(1),
                    0L,
                    0L,
                    100L,
                    1_700_000_000_000L,
                    furiten: new TableFuritenSnapshot(round.RoundId, temporaryMask: 1, riichiMask: 2)));
            Assert.True(projected.PrivateFacts[0].TemporaryFuriten);
            Assert.True(projected.PrivateFacts[0].Furiten);
            Assert.False(projected.PrivateFacts[0].RiichiFuriten);
            Assert.True(projected.PrivateFacts[1].RiichiFuriten);
            Assert.True(projected.PrivateFacts[1].Furiten);
            Assert.False(projected.PrivateFacts[2].Furiten);
        }

        [Fact]
        public async Task SubmitFailure_DoesNotPublishTerminalVersionOrSettlement()
        {
            var options = new TableServiceOptions();
            var decisionState = new TableRoomDecisionState();
            RoundStateMachine round = DealerTsumoRound("room-b2-commitfail");
            await using TableActor actor = RoomHost.CreateActor(
                "room-b2-commitfail",
                options,
                new FailingCommitPort(),
                FrozenUtcClock.Instance,
                publicationSink: null,
                new TableStreamOptions(),
                durable: null,
                new OwnerEpoch(1),
                round,
                decisionState);
            var request = new TableSettlementRequest(
                new TableSettlementIntent(round.RoundId, TableSettlementAction.Tsumo, 0),
                new RequestId("req-b2-commitfail"),
                new StateVersion(0),
                new OwnerEpoch(1),
                new ControlEpoch(0));
            TableOperationResult result = await actor.SubmitSettlementAsync(request);
            Assert.Equal(TableOperationStatus.Failed, result.Status);
            Assert.Equal("COMMIT_TEST_FAILURE", result.ReasonCode);
            Assert.Equal(0UL, result.EvaluatedStateVersion.Value);
            Assert.False(actor.GetRoundStateForHost().IsFinished);
            Assert.Equal(0, actor.GetRoundStateForHost().Version);
        }

        [Fact]
        public async Task AbortiveDraw_NineTerminals_GoesThroughTrustedSettlementAndRoundEnd()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(roomId =>
                {
                    var fixture = new N1aTable { Dealer = 0 };
                    fixture.Hand(0,
                        new TileId(0), new TileId(32),
                        new TileId(36), new TileId(68),
                        new TileId(72), new TileId(104),
                        new TileId(108), new TileId(112), new TileId(116),
                        new TileId(120), new TileId(124), new TileId(128),
                        new TileId(132));
                    fixture.Drawn(0, new TileId(1));
                    return fixture.Build(roomId + "-r1");
                }),
                clock: FrozenUtcClock.Instance);
            TableTestClient client = await host.ConnectPlayerAsync("room-b2-abortive", "acct-b2-abortive", 0);
            try
            {
                PlayerSnapshot before = await WaitForDecisionAsync(client, "room-b2-abortive-r1");
                // The frozen candidate enum has no abortive-draw value, so the service cannot
                // advertise it; it still maps the protocol action when the committed round
                // proves the reason.
                Assert.DoesNotContain(
                    before.PrivateView!.Decision.Actions,
                    a => a.ActionType == (uint)ProtocolActionType.DeclareAbortiveDraw);
                ulong version = before.StateVersion;
                string requestId = TableWire.RandomId("req-b2-abortive");
                await client.SendAsync(TableWire.Command(
                    client.Welcome!,
                    requestId,
                    Contracts.CommandKind.SubmitAction,
                    TableWire.SubmitActionPayload(
                        before.RoundId,
                        before.PrivateView!.Decision.DecisionId,
                        before.PrivateView.Decision.ControlEpoch,
                        (uint)ProtocolActionType.DeclareAbortiveDraw,
                        null),
                    expectedStateVersion: version));
                Envelope receipt = await client.ReadUntilAsync(
                    e => (e.MessageType == MessageType.Receipt && e.RequestId == requestId) ||
                         e.MessageType == MessageType.Error,
                    TimeSpan.FromSeconds(20));
                Assert.True(
                    receipt.MessageType == MessageType.Receipt,
                    "abortive draw returned " + receipt.MessageType + ": " +
                    (receipt.Error?.Code ?? string.Empty) + "/" + (receipt.Error?.DetailCode ?? string.Empty));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, receipt.Receipt.Status);
                PlayerSnapshot after = await WaitForPhaseAsync(client, GamePhase.RoundEnd);
                Assert.Equal(version + 1UL, after.StateVersion);
                Assert.Contains(
                    after.PublicView!.SettlementEvents,
                    e => e.EventType == EventType.DrawGame);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        [Fact]
        public async Task AbortiveDraw_WithoutProvableReason_FailsClosedBeforeActorWithZeroSideEffect()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(DealerTsumoRound),
                clock: FrozenUtcClock.Instance);
            TableTestClient client = await host.ConnectPlayerAsync("room-b2-noabort", "acct-b2-noabort", 0);
            try
            {
                PlayerSnapshot before = await WaitForDecisionAsync(client, "room-b2-noabort-r1");
                ulong version = before.StateVersion;
                string requestId = TableWire.RandomId("req-b2-noabort");
                await client.SendAsync(TableWire.Command(
                    client.Welcome!,
                    requestId,
                    Contracts.CommandKind.SubmitAction,
                    TableWire.SubmitActionPayload(
                        before.RoundId,
                        before.PrivateView!.Decision.DecisionId,
                        before.PrivateView.Decision.ControlEpoch,
                        (uint)ProtocolActionType.DeclareAbortiveDraw,
                        null),
                    expectedStateVersion: version));
                Envelope error = await client.ReadUntilAsync(
                    e => e.MessageType == MessageType.Error,
                    TimeSpan.FromSeconds(10));
                Assert.Equal(ProtocolErrorCode.InternalError, error.Error.Code);
                Assert.Equal(
                    TableCommandMapper.SettlementAbortiveReasonDetail,
                    error.Error.DetailCode);
                PlayerSnapshot after = await ReadSnapshotAsync(client, TimeSpan.FromSeconds(5));
                Assert.Equal(version, after.StateVersion);
                Assert.NotEqual(GamePhase.RoundEnd, after.PublicView!.Board.Phase);
                Assert.Empty(after.PublicView.SettlementEvents);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        private static TileId[] Tiles(params int[] ids)
        {
            return ids.Select(id => new TileId(id)).ToArray();
        }

        private static RoundStateMachine Apply(RoundStateMachine round, RoundCommand command)
        {
            RoundTransition transition = round.Execute(command);
            Assert.True(
                transition.IsAccepted,
                "fixture transition rejected: " + (transition.Error == null ? "unknown" : transition.Error.ToString()));
            return transition.State!;
        }

        private static RoundStateMachine ClosedKanRinshanWin(string roundId)
        {
            var fixture = new N1aTable { Dealer = 0 };
            fixture.Hand(0, Tiles(0, 1, 2, 3, 36, 40, 44, 48, 52, 56, 60, 64, 68));
            fixture.Drawn(0, new TileId(72));
            fixture.DeadWall(new TileId(73));
            return Apply(
                fixture.Build(roundId),
                RoundCommand.ClosedKan(
                    roundId, 0, new TileId(0), new TileId(1), new TileId(2), new TileId(3)));
        }

        private static RoundStateMachine OpenKanRinshanWin(string roundId)
        {
            var fixture = new N1aTable { Dealer = 1 };
            fixture.Hand(0, Tiles(
                124, 125, 126,
                40, 44, 48,
                88, 92, 96,
                24, 28,
                68, 69));
            fixture.Hand(1, new TileId(127));
            fixture.Drawn(1, new TileId(0));
            fixture.DeadWall(new TileId(32));
            RoundStateMachine round = fixture.Build(roundId);
            round = Apply(
                round,
                RoundCommand.Discard(round.RoundId, 1, new TileId(127), new[] { 0 }));
            return Apply(
                round,
                RoundCommand.OpenKan(
                    round.RoundId, 0, new TileId(127),
                    new TileId(124), new TileId(125), new TileId(126)));
        }

        private static RoundStateMachine DiscardFuritenWindow(string roundId)
        {
            var fixture = new N1aTable { Dealer = 0 };
            fixture.Hand(0, Tiles(124, 125, 126, 4, 8, 48, 52, 56, 92, 96, 100, 16, 17));
            fixture.Drawn(0, new TileId(12));
            fixture.Hand(1, new TileId(0));
            fixture.WallPrefix(new TileId(128));
            RoundStateMachine round = fixture.Build(roundId);
            round = Apply(
                round,
                RoundCommand.Discard(round.RoundId, 0, new TileId(12), Array.Empty<int>()));
            if (round.Phase == RoundPhase.Draw)
            {
                round = Apply(round, RoundCommand.Draw(round.RoundId, 1));
            }

            return Apply(
                round,
                RoundCommand.Discard(round.RoundId, 1, new TileId(0), new[] { 0 }));
        }

        private static RoundStateMachine NineTerminals(string roundId)
        {
            var fixture = new N1aTable { Dealer = 0 };
            fixture.Hand(0, Tiles(0, 32, 36, 68, 72, 104, 108, 112, 116, 120, 124, 128, 132));
            fixture.Drawn(0, new TileId(1));
            return fixture.Build(roundId + "-r1");
        }

        private static ExhaustiveFixture BuildExhaustiveFixture(string roomId)
        {
            RoundStateMachine initial = new N1aTable { Dealer = 0 }
                .Drawn(0, new TileId(12))
                .Build(roomId + "-r1");
            RoundStateMachine round = initial;
            int transitions = 0;
            while (round.Phase != RoundPhase.Draw || round.Tiles.WallRemaining != 0)
            {
                if (++transitions > 200)
                {
                    throw new InvalidOperationException("The fixture did not naturally exhaust the wall.");
                }

                if (round.Phase == RoundPhase.Draw)
                {
                    round = Apply(round, RoundCommand.Draw(round.RoundId, round.CurrentSeat));
                    continue;
                }

                DrawnTileEvidence? drawn = round.Tiles.DrawnTile;
                Assert.True(drawn.HasValue, "a discard phase must carry the authoritative drawn tile");
                round = Apply(
                    round,
                    RoundCommand.Discard(
                        round.RoundId,
                        round.CurrentSeat,
                        drawn.Value.Tile,
                        Array.Empty<int>()));
            }

            var state = new TableRoomDecisionState();
            var source = new TableProjectionContextSource(state, initial);
            return new ExhaustiveFixture(roomId, round, state, source);
        }

        private sealed class ExhaustiveFixture
        {
            public ExhaustiveFixture(
                string roomId,
                RoundStateMachine round,
                TableRoomDecisionState decisionState,
                TableProjectionContextSource source)
            {
                RoomId = roomId;
                Round = round;
                DecisionState = decisionState;
                Source = source;
            }

            public string RoomId { get; }

            public RoundStateMachine Round { get; }

            public TableRoomDecisionState DecisionState { get; }

            public TableProjectionContextSource Source { get; }
        }

        private sealed class FailOnceCommitPort : ITableCommitPort
        {
            private readonly TableServiceCommitPort _inner = new TableServiceCommitPort(new OwnerEpoch(1));
            public int Calls;

            public ValueTask<TableCommitResult> CommitAsync(
                TableCommitRequest request,
                CancellationToken cancellationToken)
            {
                if (++Calls == 1)
                {
                    return new ValueTask<TableCommitResult>(
                        TableCommitResult.Failed(
                            "COMMIT_TEST_TRANSIENT",
                            "one controlled fail-before-commit"));
                }

                return _inner.CommitAsync(request, cancellationToken);
            }
        }

        private sealed class BlockingCommitPort : ITableCommitPort
        {
            private readonly TableServiceCommitPort _inner = new TableServiceCommitPort(new OwnerEpoch(1));
            private readonly TaskCompletionSource<bool> _started =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Calls;

            public Task Started => _started.Task;

            public void Release()
            {
                _release.TrySetResult(true);
            }

            public async ValueTask<TableCommitResult> CommitAsync(
                TableCommitRequest request,
                CancellationToken cancellationToken)
            {
                Calls++;
                _started.TrySetResult(true);
                await _release.Task.ConfigureAwait(false);
                return await _inner.CommitAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class FailAlwaysCommitPort : ITableCommitPort
        {
            public int Calls;

            public ValueTask<TableCommitResult> CommitAsync(
                TableCommitRequest request,
                CancellationToken cancellationToken)
            {
                Calls++;
                return new ValueTask<TableCommitResult>(
                    TableCommitResult.Failed(
                        "COMMIT_TEST_PERSISTENT",
                        "controlled persistent fail-before-commit"));
            }
        }

        private sealed class FailingCommitPort : ITableCommitPort
        {
            public ValueTask<TableCommitResult> CommitAsync(
                TableCommitRequest request,
                CancellationToken cancellationToken)
            {
                return new ValueTask<TableCommitResult>(
                    TableCommitResult.Failed("COMMIT_TEST_FAILURE", "synthetic commit failure"));
            }
        }

        private static int CountSettlementEvents(PlayerSnapshot snapshot)
        {
            return snapshot.PublicView?.SettlementEvents.Count ?? 0;
        }

        /// <summary>
        /// Reads the production room's durable store (the in-memory implementation of the same
        /// frozen ports used by PostgreSQL) and proves the terminal outbox is unique and agrees
        /// with the committed result: round id, version, outcome and all four final scores.
        /// </summary>
        private static async Task<InMemoryTableStore> AssertSingleTerminalOutboxAsync(
            TableServiceTestHost host,
            string roomId,
            PlayerSnapshot terminal)
        {
            RoomHost room = host.GetRoomHost(roomId);
            var store = Assert.IsType<InMemoryTableStore>(room.Store);
            Assert.Equal(1, store.OutboxCount);
            var outbox = Assert.IsAssignableFrom<ISettlementOutbox>(store);
            IReadOnlyList<TableSettlementOutboxRecord> rows =
                await outbox.ListUnpublishedAsync(CancellationToken.None);
            TableSettlementOutboxRecord row = Assert.Single(rows);
            Assert.Equal(roomId, row.MatchId);
            Assert.Equal((int)terminal.StateVersion, row.ResultVersion);
            Assert.Equal(TablePayloadSha256.Compute(row.Payload), row.PayloadSha256);

            using var reader = new BinaryReader(
                new MemoryStream(row.Payload),
                Encoding.UTF8,
                leaveOpen: false);
            Assert.Equal(TableSettlementPayloadCodec.FormatVersion, reader.ReadInt32());
            Assert.Equal(roomId, reader.ReadString());
            _ = reader.ReadString(); // resultId is projected through the event, not needed here
            Assert.Equal(terminal.RoundId, reader.ReadString());
            _ = reader.ReadByte();   // outcome kind (Tsumo/Ron/Exhaustive/Abortive)
            _ = reader.ReadInt32();  // winner seat
            _ = reader.ReadInt32();  // loser seat
            int tenpai = reader.ReadInt32();
            for (int i = 0; i < tenpai; i++)
            {
                _ = reader.ReadInt32();
            }

            _ = reader.ReadString(); // outcome reason
            var finalScores = new long[4];
            for (int seat = 0; seat < 4; seat++)
            {
                finalScores[seat] = reader.ReadInt64();
                _ = reader.ReadInt64(); // total delta
            }

            Assert.Equal(
                terminal.PublicView!.Board.Scores.Select(s => s.Score).ToArray(),
                finalScores);
            Assert.Equal(100000L, finalScores.Sum());
            return store;
        }

        private static async Task<PlayerSnapshot> WaitForDecisionAsync(TableTestClient client, string roundId)
        {
            Envelope envelope = await client.ReadUntilAsync(
                e => e.PlayerUpdate?.Snapshot != null &&
                     e.PlayerUpdate.Snapshot.RoundId == roundId &&
                     e.PlayerUpdate.Snapshot.PrivateView?.Decision != null,
                TimeSpan.FromSeconds(20));
            return envelope.PlayerUpdate.Snapshot;
        }

        private static async Task<PlayerSnapshot> WaitForPhaseAsync(TableTestClient client, GamePhase phase)
        {
            Envelope envelope = await client.ReadUntilAsync(
                e => e.PlayerUpdate?.Snapshot != null &&
                     e.PlayerUpdate.Snapshot.PublicView?.Board.Phase == phase &&
                     e.PlayerUpdate.Snapshot.PublicView.SettlementEvents.Count > 0,
                TimeSpan.FromSeconds(20));
            return envelope.PlayerUpdate.Snapshot;
        }

        private static async Task<PlayerSnapshot> ReadSnapshotAsync(TableTestClient client, TimeSpan timeout)
        {
            Envelope envelope = await client.ReadUntilAsync(
                e => e.PlayerUpdate?.Snapshot != null,
                timeout);
            return envelope.PlayerUpdate.Snapshot;
        }
        private sealed class FrozenUtcClock : ITableClock
        {
            public static readonly FrozenUtcClock Instance = new FrozenUtcClock();
            private static readonly long Start = Stopwatch.GetTimestamp();

            public long MonotonicMillis =>
                (long)((Stopwatch.GetTimestamp() - Start) * 1000.0 / Stopwatch.Frequency);

            // A fixed UTC origin makes the terminal checkpoint's timing payload and metadata
            // agree deterministically. Production uses SystemTableClock and is exercised by
            // the separate formal run; the TableRuntime terminal-commit race is reported there.
            public long UtcNowMs => 1_700_000_000_000L;
        }

        /// <summary>
        /// Advance UTC by one millisecond on every read. Used only to make the production
        /// two-read terminal-checkpoint defect deterministic for the fail-closed negative test.
        /// </summary>
        private sealed class AdvancingUtcClock : ITableClock
        {
            private long _utc = 1_700_000_000_000L;
            private readonly long _start = Stopwatch.GetTimestamp();

            public long MonotonicMillis =>
                (long)((Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency);

            public long UtcNowMs => Interlocked.Increment(ref _utc);
        }
    }
}
