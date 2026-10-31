using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// Round-3 review of D-1-01 against the real Kestrel host + the real actor runtime: the
    /// honest legal candidate view (riichi advertised for a provably tenpai hand), the riichi
    /// lock (EXACTLY the seat's tsumogiri candidate afterwards), the trusted response-window
    /// fence (a legitimate pon gets an authoritative window and an APPLIED claim), the trusted
    /// draw phase (no invented client decision; the table itself advances every draw through
    /// the actor's own durable pipeline), the initial deal-extra discard's exact tsumogiri
    /// fact, and zero side effects for a riichi-locked foreign discard.
    /// </summary>
    public sealed class ReviewRound3DecisionRuntimeTests
    {
        /// <summary>The review's own "N1aTable" tenpai hand: a Domain-confirmed riichi-able
        /// seat 0 whose initial drawn tile (80 = pin 3) proves the hidden deal-extra truth; the
        /// neighbour seats carry no legal claim fit against any of the loop's discards.</summary>
        private static RoundStateMachine RiichiLoopRound(string roomId)
        {
            var fixture = new N1aTable { Dealer = 0 };
            // Seat 0 (review's exact shape): man 1..9, pin1 x2, sou5, sou6, drawn pin3 (80).
            var seat0 = new List<uint>();
            for (uint tile = 0; tile <= 8; tile++)
            {
                seat0.Add(tile);
            }

            seat0.AddRange(new[] { 72u, 73u, 52u, 53u });
            fixture.Hand(0, seat0.Select(value => new TileId((int)value)).ToArray());
            fixture.Drawn(0, new TileId(80));
            // Seat 1: pin ranks {1,4,7} x4 (its own discard tiles), no claim fit anywhere.
            fixture.Hand(1, HandRanks(new uint[] { 36, 48, 60 }));
            // Seat 2: sou ranks {3,6,9} x4.
            fixture.Hand(2, HandRanks(new uint[] { 44, 56, 68 }));
            // Seat 3: pin ranks {2,5,8} x4.
            fixture.Hand(3, HandRanks(new uint[] { 76, 88, 100 }));
            return fixture.Build(roomId + "-r1");
        }

        private static TileId[] HandRanks(uint[] firstCopyValues)
        {
            var tiles = new List<TileId>();
            foreach (uint first in firstCopyValues)
            {
                for (int copy = 0; copy < 4; copy++)
                {
                    tiles.Add(new TileId((int)first + copy));
                }
            }

            return tiles.ToArray();
        }

        [Fact]
        public async Task RiichiLoop_RiichiAdvertised_LockedTsumogiriCarriesTheInitialFact_TwoFullRotations()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(RiichiLoopRound));
            var clients = new List<TableTestClient>();
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    clients.Add(await host.ConnectPlayerAsync("room-riichi", "acct-round3-" + seat, seat));
                }

                PlayerSnapshot first = await WaitForTurnDecisionAsync(clients[0], 0);
                // The honest candidate view: the tenpai hand's riichi IS advertised (the exact
                // review positive). The candidate set mirrors the Domain's own
                // CanRiichiWithTile — every legal riichi+discard pairing is advertised, and
                // the seat's own drawn tile 80 is one of them (the deal-extra evidence).
                Assert.Contains(
                    first.PrivateView!.Decision!.Actions,
                    action => action.ActionType == (uint)ProtocolActionType.DeclareRiichi &&
                              action.TileIds[0] == 80u);
                Assert.Contains(
                    first.PrivateView!.Decision!.Actions,
                    action => action.ActionType == (uint)ProtocolActionType.DiscardTile &&
                              action.TileIds[0] == 80u);

                // The legal riichi through the published decision id: APPLIED through the actor.
                Envelope riichiReceipt = await SubmitAndReadAsync(
                    clients[0], first, ProtocolActionType.DeclareRiichi, new uint[] { 80 });
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, riichiReceipt.Receipt.Status);
                Assert.True(
                    riichiReceipt.Receipt.CommandVersion > first.StateVersion,
                    "the legal riichi grows the authoritative chain");

                // The routed neighbour turn: the initial discard carries the DEAL-EXTRA
                // tsumogiri fact of the Domain's closed world plus its riichi marker.
                // Waited on the PUBLIC river only — a decision-shaped wait here would
                // consume seat 1's discard-turn decision as a peek and the rotation loop
                // below would starve on it (the publish path twins every decision).
                PlayerSnapshot seat1River = await WaitForPlayerSnapshotAsync(
                    clients[1],
                    item => item.MessageType == MessageType.PlayerUpdate &&
                            item.PlayerUpdate.Snapshot.PublicView!.Board.Players[0].Discards.Count == 1);
                Contracts.Wire.DiscardView seat0Discard = Assert.Single(
                    seat1River.PublicView!.Board.Players[0].Discards);
                Assert.True(seat0Discard.Tsumogiri, "the riichi discard of the drawn tile 80 is a tsumogiri");
                Assert.True(seat0Discard.RiichiDiscard, "the discard is the seat's own declared riichi discard");
                Assert.Equal(80u, seat0Discard.TileId);

                // The riichi LOCK: seat 0's next turn advertises EXACTLY one candidate, its
                // locked discard applies, its newest river entry is again the exact tsumogiri;
                // seats 1..3 discard their legal hand candidates. Two full rotations.
                for (int rotation = 0; rotation < 2; rotation++)
                {
                    for (int delta = 1; delta <= 4; delta++)
                    {
                        int seat = (0 + delta) % 4;
                        PlayerSnapshot turnSnapshot = await WaitForTurnDecisionAsync(clients[seat], seat);
                        if (seat == 0)
                        {
                            Assert.Single(turnSnapshot.PrivateView!.Decision!.Actions);
                            Assert.Equal(
                                (uint)ProtocolActionType.DiscardTile,
                                turnSnapshot.PrivateView!.Decision!.Actions[0].ActionType);
                        }

                        uint chosen = turnSnapshot.PrivateView!.Decision!.Actions[0].TileIds[0];
                        Envelope receipt = await SubmitAndReadAsync(
                            clients[seat], turnSnapshot, ProtocolActionType.DiscardTile, new[] { chosen });
                        Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, receipt.Receipt.Status);
                        Assert.False(receipt.Receipt.Replayed);
                    }

                    // The seat-0 discard of this rotation must again be its locked tsumogiri.
                    // Waited on the PUBLIC river count only: a decision-shaped wait here
                    // would consume seat 1's NEXT turn decision as a peek and starve the
                    // next rotation's turn wait (the publish path twins every decision).
                    PlayerSnapshot after = await WaitForPlayerSnapshotAsync(
                        clients[1],
                        item => item.MessageType == MessageType.PlayerUpdate &&
                                item.PlayerUpdate.Snapshot.PublicView!.Board.Players[0].Discards.Count ==
                                    (rotation == 0 ? 2 : 3));
                    Contracts.Wire.DiscardView lastSeat0Discard = after.PublicView!.Board.Players[0].Discards[^1];
                    Assert.True(
                        lastSeat0Discard.Tsumogiri,
                        "the locked riichi seat keeps discarding exactly its drawn tile");
                    Assert.Equal(
                        rotation == 0 ? 2 : 3,
                        (int)after.PublicView!.Board.Players[0].Discards.Count);
                }
            }
            finally
            {
                foreach (TableTestClient client in clients)
                {
                    await client.DisposeAsync();
                }
            }
        }

        /// <summary>The riichi-locked seat is LOCKED against its own other hand tiles: the
        /// published candidate set is exactly one tile; a genuinely held foreign hand tile
        /// never applies and the chain carries zero side effects.</summary>
        [Fact]
        public async Task RiichiLock_ForeignTileDiscardNeverApplies()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(RiichiLoopRound));
            var clients = new List<TableTestClient>();
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    clients.Add(await host.ConnectPlayerAsync("room-rilock", "acct-lock-" + seat, seat));
                }

                PlayerSnapshot first = await WaitForTurnDecisionAsync(clients[0], 0);
                Envelope riichiReceipt = await SubmitAndReadAsync(
                    clients[0], first, ProtocolActionType.DeclareRiichi, new uint[] { 80 });
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, riichiReceipt.Receipt.Status);
                for (int seat = 1; seat <= 3; seat++)
                {
                    PlayerSnapshot turn = await WaitForTurnDecisionAsync(clients[seat], seat);
                    uint chosen = turn.PrivateView!.Decision!.Actions[0].TileIds[0];
                    Envelope discard = await SubmitAndReadAsync(
                        clients[seat], turn, ProtocolActionType.DiscardTile, new[] { chosen });
                    Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, discard.Receipt.Status);
                }

                PlayerSnapshot locked = await WaitForTurnDecisionAsync(clients[0], 0);
                uint lockedTile = locked.PrivateView!.Decision!.Actions[0].TileIds[0];

                // A hand tile that IS genuinely held but is NOT the locked drawn tile: the
                // Domain refuses at commit, the chain does not move, nothing is published.
                uint other = locked.PrivateView!.ClosedTiles.First(tile => tile != lockedTile);
                byte[] payload = TableWire.SubmitActionPayload(
                    locked.RoundId, locked.PrivateView!.Decision!.DecisionId,
                    locked.PrivateView!.Decision!.ControlEpoch,
                    (uint)ProtocolActionType.DiscardTile, new[] { other });
                await clients[0].SendAsync(TableWire.Command(
                    clients[0].Welcome!, TableWire.RandomId("locked"),
                    Contracts.CommandKind.SubmitAction, payload, expectedStateVersion: locked.StateVersion));
                Envelope refused = await clients[0].ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt, TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Rejected, refused.Receipt.Status);
                Assert.Equal(locked.StateVersion, refused.Receipt.EvaluatedStateVersion);

                // The locked tile through the SAME decision id: applied, and the river grows.
                Envelope ok = await SubmitAndReadAsync(
                    clients[0], locked, ProtocolActionType.DiscardTile, new[] { lockedTile });
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, ok.Receipt.Status);
                Assert.True(ok.Receipt.CommandVersion > refused.Receipt.EvaluatedStateVersion);
            }
            finally
            {
                foreach (TableTestClient client in clients)
                {
                    await client.DisposeAsync();
                }
            }
        }

        /// <summary>Seat 1 holds two physical copies of the white type whose next copy is just
        /// discarded: the discard command carries the TRUSTED window (never a client claim),
        /// the claim seat sees an executable PON candidate, and the claim applies.</summary>
        [Fact]
        public async Task PonWindow_OpensThroughTrustedEligibility_ClaimApplies()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(roomId =>
                {
                    var fixture = new N1aTable { Dealer = 0 };
                    // The pair plus eleven disconnected singles cannot complete a hand with
                    // the discarded 127; this test isolates the executable pon window and
                    // must not accidentally advertise the new Ron candidate.
                    fixture.Hand(1,
                        new TileId(124), new TileId(125),
                        new TileId(0), new TileId(12), new TileId(24),
                        new TileId(36), new TileId(48), new TileId(60),
                        new TileId(72), new TileId(84), new TileId(96),
                        new TileId(108), new TileId(112));
                    fixture.Drawn(0, new TileId(127));
                    return fixture.Build(roomId + "-r1");
                }));
            var clients = new List<TableTestClient>();
            try
            {
                clients.Add(await host.ConnectPlayerAsync("room-pon", "acct-pon-0", 0));
                clients.Add(await host.ConnectPlayerAsync("room-pon", "acct-pon-1", 1));

                PlayerSnapshot first = await WaitForTurnDecisionAsync(clients[0], 0);
                Envelope discard = await SubmitAndReadAsync(
                    clients[0], first, ProtocolActionType.DiscardTile, new uint[] { 127 });
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, discard.Receipt.Status);

                // The trusted window: seat 1's own decision view advertises an executable pon.
                PlayerSnapshot window = await WaitForPlayerSnapshotAsync(
                    clients[1],
                    item => item.PlayerUpdate.Snapshot.PrivateView?.Decision != null &&
                            item.PlayerUpdate.Snapshot.PublicView!.Board.CurrentSeat == 0 &&
                            item.PlayerUpdate.Snapshot.PublicView!.Board.Players[0].Discards.Count == 1);
                Assert.Contains(
                    window.PrivateView!.Decision!.Actions,
                    action => action.ActionType == (uint)ProtocolActionType.DeclarePon);
                Assert.Equal(127u, window.PublicView!.Board.Players[0].Discards[0].TileId);
                // Pass plus the executable pon (no kan, no chi in this window).
                Contracts.Wire.DecisionView seat1Decision = window.PrivateView!.Decision!;
                Assert.Equal(
                    new HashSet<uint> { (uint)ProtocolActionType.Pass, (uint)ProtocolActionType.DeclarePon },
                    seat1Decision.Actions.Select(action => action.ActionType).ToHashSet());

                // The claim applies through the frozen three-tile wire command.
                Envelope pon = await SubmitAndReadAsync(
                    clients[1], window, ProtocolActionType.DeclarePon, new uint[] { 127, 124, 125 });
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, pon.Receipt.Status);
                Assert.True(pon.Receipt.CommandVersion > window.StateVersion);

                // The meld record reaches the neighbour projection as one trusted seat-1 pon:
                // exactly the three physical tiles of the claim, claimed copy included.
                PlayerSnapshot afterMeld = await WaitForPlayerSnapshotAsync(
                    clients[1],
                    item => item.PlayerUpdate.Snapshot.PublicView!.Board.Players[1].Melds.Count == 1);
                Contracts.Wire.MeldView ponMeld = afterMeld.PublicView!.Board.Players[1].Melds[0];
                Assert.Equal(3, ponMeld.TileIds.Count);
                Assert.Contains(127u, ponMeld.TileIds);
            }
            finally
            {
                foreach (TableTestClient client in clients)
                {
                    await client.DisposeAsync();
                }
            }
        }

        /// <summary>Phase DRAW turns are TRUSTED SERVICE actions: no invented empty candidate
        /// decision ever appears; the table itself advances the draw through the actor's own
        /// durable pipeline, and the drawn tile reaches the acting seat's own hand as a real
        /// committed wall draw before its discard turn starts.</summary>
        [Fact]
        public async Task DrawPhase_IsTrustedServiceAdvance_NeverAnEmptyClientDecision()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(roomId => new N1aTable { Dealer = 0 }.Build(roomId + "-r1", RoundPhase.Draw)));
            try
            {
                TableTestClient client = await host.ConnectPlayerAsync("room-draw", "acct-draw-0", 0);
                Envelope baseline = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.PlayerUpdate &&
                                envelope.PlayerUpdate.IsFullSnapshot,
                    TimeSpan.FromSeconds(20));
                PlayerSnapshot baselineSnapshot = baseline.PlayerUpdate.Snapshot;
                Assert.Null(baselineSnapshot.PrivateView!.Decision);
                Assert.Equal(0UL, baselineSnapshot.StateVersion);
                Assert.Equal(13, baselineSnapshot.PrivateView!.ClosedTiles.Count);

                // The trusted draw: at the tick cadence a committed draw arrives (its own
                // command version) and the acting seat's CHEAT_WINDOW discard turn opens with
                // the real 14th tile -- the wire view exposes the closed hand (13) plus the
                // drawn tile as its own evidence field, and the discard candidates carry the
                // drawn tile itself (the tsumogiri fence's provable tile) -- never an empty
                // decision.
                PlayerSnapshot after = await WaitForTurnDecisionAsync(client, 0);
                Assert.True(
                    after.StateVersion > baselineSnapshot.StateVersion,
                    "the auto-advance owns a real committed state version");
                Assert.Equal(13, after.PrivateView!.ClosedTiles.Count);
                uint drawnTile = after.PrivateView!.DrawnTileId;
                Assert.NotEqual(0u, drawnTile);
                Assert.Contains(
                    after.PrivateView!.Decision!.Actions,
                    action => action.ActionType == (uint)ProtocolActionType.DiscardTile &&
                              action.TileIds[0] == drawnTile);

                // The turn loop keeps moving with a legal discard; the next seat takes over.
                Envelope discard = await SubmitAndReadAsync(
                    client, after, ProtocolActionType.DiscardTile,
                    new[] { after.PrivateView!.Decision!.Actions[0].TileIds[0] });
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, discard.Receipt.Status);
                await using TableTestClient seatOne = await host.ConnectPlayerAsync("room-draw", "acct-draw-1", 1);
                PlayerSnapshot next = await WaitForTurnDecisionAsync(seatOne, 1, seconds: 40);
                Assert.Equal(1, next.PublicView!.Board.CurrentSeat);
                Assert.Single(next.PublicView!.Board.Players[0].Discards);
                await seatOne.DisposeAsync();
            }
            finally
            {
            }
        }

        /// <summary>The direct timing probe the reviewer ran on an injected clock: after the
        /// trusted drive has already advanced the draw chain, the discard turn's deadline
        /// expiry commits an authoritative timeout action instead of a frozen
        /// NO_DEFAULT_ACTION hang.</summary>
        [Fact]
        public async Task DrawDeadline_InjectedClock_TimeoutCommitsAnAuthoritativeAction()
        {
            var clock = new ProbeClock { Now = 100 };
            var registry = new InMemoryTableHostRegistry(
                new TableServiceOptions { LeaseTtlSeconds = 90 },
                new WireTableStreamContractCodec(),
                clock,
                new FixedRoundSource(roomId => new N1aTable { Dealer = 0 }.Build(roomId + "-r1", RoundPhase.Draw)));
            RoomHost host = registry.GetOrAdd("drawdeadline");
            try
            {
                var capture = new CaptureSink();
                await host.Actor.SubscribeStreamAsync(new TableStreamSubscribeRequest(
                    new TableStreamIdentity(
                        new SessionId("drawdeadline"),
                        TableStreamAudience.Spectator,
                        new BindingEpoch(1),
                        new SyncEpoch(1)),
                    host.OwnerEpoch,
                    recoveryGeneration: 0,
                    capture));
                await capture.Frames.Reader.ReadAsync();
                TableTimingSnapshot before = await host.Actor.GetTimingSnapshotAsync();
                Assert.Equal(0UL, before.StateVersion.Value);

                // The trusted draw happens on the drive itself -- the reviewer's expected
                // positive chain version (not a NO_DEFAULT_ACTION decision-replay).
                await host.DecisionState.DriveAsync(host);
                TableTimingSnapshot afterAutoDraw = await host.Actor.GetTimingSnapshotAsync();
                Assert.Equal(1UL, afterAutoDraw.StateVersion.Value);

                // Wait for the commit's own projected frame (the trusted source's capture sees
                // the new cascade CHEAT_WINDOW turn), then drive the decision open.
                await capture.Frames.Reader.ReadAsync();
                await host.DecisionState.DriveAsync(host);

                clock.Now = 24_000;
                TableTickResult tick = await host.Actor.TickAsync();
                TableTimingSnapshot afterDeadline = await host.Actor.GetTimingSnapshotAsync();
                Assert.True(
                    afterDeadline.StateVersion.Value > 1,
                    "the discard turn deadline expiry commits a real timeout action (stateVersion " +
                    afterDeadline.StateVersion.Value + ")");
                Assert.DoesNotContain(
                    tick.Entries,
                    entry => entry.RejectionCode == TickRejectionCode.NoDefaultAction);
                Assert.True(
                    tick.Entries.Count == 0 ||
                    tick.Entries.Any(entry =>
                        entry.Kind == TickEntryKind.Timeout &&
                        entry.Action != null),
                    "the timeout carries an executable fallback action " +
                    System.Text.Json.JsonSerializer.Serialize(tick.Entries));
            }
            finally
            {
                await host.Actor.DisposeAsync();
            }
        }

        private static async Task<PlayerSnapshot> WaitForTurnDecisionAsync(
            TableTestClient client,
            int seat,
            int seconds = 30)
        {
            // The publish path delivers each open decision twice (delta plus full-snapshot
            // twin); a seat's NEXT-decision wait must never resurface a decision a previous
            // wait on this client already returned for the seat — that twin is the stale
            // candidate set of an older turn.
            PlayerSnapshot snapshot = await WaitForPlayerSnapshotAsync(
                client,
                item => item.MessageType == MessageType.PlayerUpdate &&
                        item.PlayerUpdate.Snapshot.PrivateView?.Decision != null &&
                        (int)item.PlayerUpdate.Snapshot.PublicView!.Board.CurrentSeat == seat &&
                        !client.IsDecisionSeen(
                            seat, item.PlayerUpdate.Snapshot.PrivateView.Decision.DecisionId),
                seconds);
            client.MarkDecisionSeen(seat, snapshot.PrivateView!.Decision!.DecisionId);
            return snapshot;
        }

        private static async Task<PlayerSnapshot> WaitForPlayerSnapshotAsync(
            TableTestClient client,
            Func<Envelope, bool> predicate,
            int seconds = 30)
        {
            long deadline = Environment.TickCount64 + seconds * 1000L;
            while (true)
            {
                int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
                Envelope envelope = await client.ReadUntilAsync(predicate, TimeSpan.FromMilliseconds(remaining));
                return envelope.PlayerUpdate.Snapshot;
            }
        }

        private static async Task<Envelope> SubmitAndReadAsync(
            TableTestClient client,
            PlayerSnapshot snapshot,
            ProtocolActionType actionType,
            uint[] tileIds)
        {
            string requestId = TableWire.RandomId("r3");
            byte[] payload = TableWire.SubmitActionPayload(
                snapshot.RoundId,
                snapshot.PrivateView!.Decision!.DecisionId,
                snapshot.PrivateView!.Decision!.ControlEpoch,
                (uint)actionType,
                tileIds);
            await client.SendAsync(TableWire.Command(
                client.Welcome!, requestId,
                Contracts.CommandKind.SubmitAction, payload,
                expectedStateVersion: snapshot.StateVersion));
            return await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Receipt &&
                            envelope.RequestId == requestId,
                TimeSpan.FromSeconds(20));
        }

        /// <summary>Plan-timing probe clock the reviewer used with the real actor surface.</summary>
        private sealed class ProbeClock : ITableClock
        {
            public long Now = 100;

            public long MonotonicMillis => Now;

            public long UtcNowMs => 1_760_000_000_000L + Now;
        }

        private sealed class CaptureSink : ITableStreamSink
        {
            public System.Threading.Channels.Channel<TableStreamFrame> Frames =
                System.Threading.Channels.Channel.CreateUnbounded<TableStreamFrame>();

            public ValueTask PublishAsync(TableStreamFrame frame, CancellationToken cancellationToken)
            {
                return Frames.Writer.WriteAsync(frame, cancellationToken);
            }
        }
    }
}
