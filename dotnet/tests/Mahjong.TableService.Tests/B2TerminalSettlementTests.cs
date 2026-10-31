using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using System.Diagnostics;
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

                async Task AssertRejectedAsync(string label, byte[] payload, ulong expectedVersion)
                {
                    string requestId = TableWire.RandomId("req-b2-neg-" + label);
                    await client.SendAsync(TableWire.Command(
                        client.Welcome!,
                        requestId,
                        Contracts.CommandKind.SubmitAction,
                        payload,
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
    }
}
