using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// D-1-01 acceptance against the REAL Kestrel host and the REAL actor runtime: a room
    /// served by this build runs the Domain's actual draw/discard turn loop — the service
    /// opens the currently required Domain decision, the wire DecisionView carries the real
    /// decision id/epoch/actions/deadline, an ordinary legal action is strictly APPLIED with a
    /// real chained state version, the board advances to the next seat through the Domain's
    /// own auto-draw, and a same-key retry is answered from the receipt ledger without a
    /// second application.
    /// </summary>
    public sealed class RuntimeDecisionRuntimeTests
    {
        [Fact]
        public async Task RealRoomDecision_LegalDiscardIsAppliedAndTurnAdvances()
        {
            // D-5-01: the rotation path must not depend on the random deal. The production
            // cryptographic shuffle stays the default source; THIS test pins a real Domain
            // round whose discarded tile cannot open a response window — seat 0 opens with
            // thirteen honour tiles (first discard = East) and the fixture filler leaves
            // seats 1-3 all-number hands, so no seat holds a pon copy and honours never
            // chi. A claim-free discard is exactly the scenario the assertions describe:
            // the Domain's own auto-draw owns the turn advance to seat 1.
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(roomId =>
                {
                    var fixture = new N1aTable { Dealer = 0 };
                    fixture.Hand(0, new[]
                    {
                        new TileId(108), new TileId(109), new TileId(110), new TileId(111),
                        new TileId(112), new TileId(113), new TileId(114), new TileId(115),
                        new TileId(116), new TileId(117), new TileId(118), new TileId(119),
                        new TileId(120)
                    });
                    fixture.Drawn(0, new TileId(127));
                    return fixture.Build(roomId + "-r1");
                }));
            TableTestClient client = await host.ConnectPlayerAsync("room-loop", "acct-loop-0", 0);
            try
            {
                // The acting seat sees its REAL open decision (opened by the platform tick
                // drive from the Domain's own committed round state, published in the owner's
                // private view with the decision id, control epoch, candidates and deadline).
                Contracts.Wire.PlayerSnapshot snapshot = await ReadPlayerSnapshotAsync(
                    client,
                    predicate: s => s.PrivateView?.Decision != null);
                Contracts.Wire.DecisionView decision = RequireDecision(snapshot, seat: 0);
                Assert.Equal("room-loop-r1", snapshot.RoundId);
                Assert.True(decision.DeadlineRemainingMs > 0, "the open decision has a live deadline window");
                Assert.NotEmpty(decision.Actions);
                // The fixture's claim-free discard, pinned: East copy #1.
                Assert.Equal(108u, decision.Actions[0].TileIds[0]);
                int tilesBefore = TilePopulationTotal(snapshot.PrivateView!, snapshot.PublicView!);
                ulong stateVersionBefore = snapshot.StateVersion;
                // TURN 1: the legal discard through the authoritative decision id.
                string requestId = TableWire.RandomId("req");
                byte[] payload = TableWire.SubmitActionPayload(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    (uint)ProtocolActionType.DiscardTile,
                    new uint[] { decision.Actions[0].TileIds[0] });
                await client.SendAsync(TableWire.Command(
                    client.Welcome!, requestId,
                    Contracts.CommandKind.SubmitAction, payload,
                    expectedStateVersion: snapshot.StateVersion));

                Envelope receipt = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt &&
                                envelope.RequestId == requestId,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, receipt.Receipt.Status);
                Assert.False(receipt.Receipt.Replayed);
                Assert.True(
                    receipt.Receipt.CommandVersion >= stateVersionBefore + 1UL,
                    "an applied legal action must grow the authoritative chain: commandVersion " +
                    receipt.Receipt.CommandVersion + " vs base " + stateVersionBefore);

                // Same-key replay: identical request id + payload answers the durable receipt
                // without re-applying anything to the authority.
                await client.SendAsync(TableWire.Command(
                    client.Welcome!, requestId,
                    Contracts.CommandKind.SubmitAction, payload,
                    expectedStateVersion: snapshot.StateVersion));
                Envelope replay = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt &&
                                envelope.RequestId == requestId,
                    TimeSpan.FromSeconds(20));
                Assert.True(
                    replay.Receipt.Replayed,
                    "an exact same-key retry must be answered from the receipt ledger");
                Assert.Equal(receipt.Receipt.CommandVersion, replay.Receipt.CommandVersion);

                // The applied commit advanced the Domain automatically: a FRESH admission's
                // baseline carries the post-commit truth (the authoritative version grew and
                // the next seat took the turn through the Domain's own wall draw), and the
                // projection keeps the 136-tile closed world.
                await using TableTestClient observer = await host.ConnectPlayerAsync(
                    "room-loop", "acct-loop-observer", 2);
                Contracts.Wire.PlayerSnapshot after = await ReadPlayerSnapshotAsync(
                    observer,
                    predicate: s => s.StateVersion >= receipt.Receipt.CommandVersion &&
                                    s.PrivateView != null &&
                                    (int)s.PublicView!.Board.CurrentSeat == 1);
                Assert.True(
                    receipt.Receipt.CommandVersion >= stateVersionBefore + 1UL,
                    "the applied commit must own its own command version");
                Assert.True(
                    after.StateVersion >= receipt.Receipt.CommandVersion,
                    "the fresh baseline reflects the accepted commit");
                Assert.Equal(1, (int)after.PublicView!.Board.CurrentSeat);
                // The accepted discard is a REAL domain transition: seat 0's public river
                // carries exactly one discard and its public hand count dropped by one
                // (14 -> 13 through the honest projection).
                Assert.Single(after.PublicView.Board.Players[0].Discards);
                Assert.Equal(
                    snapshot.PublicView!.Board.Players[0].HandCount - 1,
                    after.PublicView.Board.Players[0].HandCount);
                Assert.True(
                    TilePopulationTotal(after.PrivateView!, after.PublicView) <= 136,
                    "the published identities never leave the 136-tile closed world");
                await observer.DisposeAsync();
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        /// <summary>
        /// D-5-01: the deterministic claim-path twin of
        /// <see cref="RealRoomDecision_LegalDiscardIsAppliedAndTurnAdvances"/>. Seat 2 holds
        /// the only two remaining copies of seat 0's first discard (man-1), so the applied
        /// discard opens a REAL response window instead of advancing: the turn stays at
        /// seat 0 while the window is open, the claimant's decision view carries exactly
        /// Pass + the executable pon of the discarded type, the claimant resolves the
        /// window through the PUBLISHED decision's PASS, and only then does the Domain's
        /// auto-draw hand the turn to seat 1 — with the receipt ledger, river and
        /// meld-free projection all consistent.
        /// </summary>
        [Fact]
        public async Task RealRoomDecision_ResponseWindowPassResolvesClaimAndTurnAdvances()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(roomId =>
                {
                    // The caller-proved deterministic window deal: seat 0's first candidate
                    // is man-1 copy #1 (the filler hand starts at tile 0) and seat 2 holds
                    // copies #2 and #3. No other seat can pon, and seat 1 holds no
                    // man-2/man-3 run pair, so this window has exactly one claimant.
                    var fixture = new N1aTable { Dealer = 0 };
                    fixture.Drawn(0, new TileId(127));
                    fixture.Hand(2, new TileId(1), new TileId(2));
                    return fixture.Build(roomId + "-r1");
                }));
            TableTestClient client = await host.ConnectPlayerAsync("room-window", "acct-window-0", 0);
            TableTestClient claimant = await host.ConnectPlayerAsync("room-window", "acct-window-2", 2);
            TableTestClient next = await host.ConnectPlayerAsync("room-window", "acct-window-1", 1);
            try
            {
                // TURN 1: seat 0's legal discard of man-1 copy #1, pinned by the fixture.
                Contracts.Wire.PlayerSnapshot snapshot = await ReadPlayerSnapshotAsync(
                    client,
                    predicate: s => s.PrivateView?.Decision != null);
                Contracts.Wire.DecisionView decision = RequireDecision(snapshot, seat: 0);
                Assert.Equal("room-window-r1", snapshot.RoundId);
                Assert.NotEmpty(decision.Actions);
                Assert.Equal(0u, decision.Actions[0].TileIds[0]);
                ulong stateVersionBefore = snapshot.StateVersion;
                string requestId = TableWire.RandomId("win");
                byte[] payload = TableWire.SubmitActionPayload(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    (uint)ProtocolActionType.DiscardTile,
                    new uint[] { decision.Actions[0].TileIds[0] });
                await client.SendAsync(TableWire.Command(
                    client.Welcome!, requestId,
                    Contracts.CommandKind.SubmitAction, payload,
                    expectedStateVersion: snapshot.StateVersion));
                Envelope receipt = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt &&
                                envelope.RequestId == requestId,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, receipt.Receipt.Status);
                Assert.False(receipt.Receipt.Replayed);
                Assert.True(
                    receipt.Receipt.CommandVersion >= stateVersionBefore + 1UL,
                    "an applied legal action must grow the authoritative chain");

                // The REAL response window: the turn has NOT advanced — the claimant's own
                // decision view still shows currentSeat 0 with the discard in the river,
                // and carries exactly Pass plus the executable pon of the discarded type.
                Contracts.Wire.PlayerSnapshot window = await ReadPlayerSnapshotAsync(
                    claimant,
                    predicate: s => s.PrivateView?.Decision != null &&
                                    (int)s.PublicView!.Board.CurrentSeat == 0 &&
                                    s.PublicView!.Board.Players[0].Discards.Count == 1);
                Contracts.Wire.DecisionView claimDecision = window.PrivateView!.Decision!;
                Assert.Equal(
                    new HashSet<uint> { (uint)ProtocolActionType.Pass, (uint)ProtocolActionType.DeclarePon },
                    claimDecision.Actions.Select(action => action.ActionType).ToHashSet());
                Assert.Equal(
                    0u,
                    claimDecision.Actions
                        .First(action => action.ActionType == (uint)ProtocolActionType.DeclarePon)
                        .TileIds[0]);

                // Resolve the window through the PUBLISHED decision: the claimant passes.
                string passId = TableWire.RandomId("pass");
                byte[] passPayload = TableWire.SubmitActionPayload(
                    window.RoundId,
                    claimDecision.DecisionId,
                    claimDecision.ControlEpoch,
                    (uint)ProtocolActionType.Pass);
                await claimant.SendAsync(TableWire.Command(
                    claimant.Welcome!, passId,
                    Contracts.CommandKind.SubmitAction, passPayload,
                    expectedStateVersion: window.StateVersion));
                Envelope passReceipt = await claimant.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt &&
                                envelope.RequestId == passId,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, passReceipt.Receipt.Status);
                Assert.False(passReceipt.Receipt.Replayed);
                Assert.True(
                    passReceipt.Receipt.CommandVersion >= window.StateVersion + 1UL,
                    "the window resolution must own a real committed state version");

                // Same-key replay of the pass: answered from the receipt ledger without a
                // second application.
                await claimant.SendAsync(TableWire.Command(
                    claimant.Welcome!, passId,
                    Contracts.CommandKind.SubmitAction, passPayload,
                    expectedStateVersion: window.StateVersion));
                Envelope passReplay = await claimant.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt &&
                                envelope.RequestId == passId,
                    TimeSpan.FromSeconds(20));
                Assert.True(
                    passReplay.Receipt.Replayed,
                    "an exact same-key retry of the pass must be answered from the receipt ledger");
                Assert.Equal(passReceipt.Receipt.CommandVersion, passReplay.Receipt.CommandVersion);

                // Only NOW the Domain's auto-draw hands the turn to seat 1: the next seat
                // owns a real discard decision through its own projection.
                Contracts.Wire.PlayerSnapshot after = await ReadPlayerSnapshotAsync(
                    next,
                    predicate: s => (int)s.PublicView!.Board.CurrentSeat == 1 &&
                                    s.PrivateView?.Decision != null);
                Assert.True(
                    after.StateVersion >= passReceipt.Receipt.CommandVersion,
                    "the fresh baseline reflects the window resolution commit");
                Assert.Equal(1, (int)after.PublicView!.Board.CurrentSeat);
                // The window resolved without a claim: the river holds exactly the one
                // discard, no meld ever appeared, and the projection stays inside the
                // 136-tile closed world.
                Assert.Single(after.PublicView.Board.Players[0].Discards);
                Assert.Equal(0u, after.PublicView.Board.Players[0].Discards[0].TileId);
                Assert.Empty(after.PublicView.Board.Players[2].Melds);
                Assert.True(
                    TilePopulationTotal(after.PrivateView!, after.PublicView) <= 136,
                    "the published identities never leave the 136-tile closed world");
            }
            finally
            {
                await client.DisposeAsync();
                await claimant.DisposeAsync();
                await next.DisposeAsync();
            }
        }

        [Fact]
        public async Task IllegalAction_AndWrongDecisionCannotAdvanceTheAuthority()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-neg", "acct-neg", 0);
            try
            {
                Contracts.Wire.PlayerSnapshot snapshot =
                    await ReadPlayerSnapshotAsync(client, predicate: s => s.PrivateView?.Decision != null);
                Contracts.Wire.DecisionView decision = RequireDecision(snapshot, seat: 0);
                ulong stateVersion = snapshot.StateVersion;

                // A legal-LOOKING discard that names a tile the seat does not hold.
                uint ownTile = decision.Actions[0].TileIds[0];
                uint foreignTile = TileOutOfRange(snapshot, ownTile);
                byte[] payload = TableWire.SubmitActionPayload(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    (uint)ProtocolActionType.DiscardTile,
                    new uint[] { foreignTile });
                await client.SendAsync(TableWire.Command(
                    client.Welcome!, TableWire.RandomId("neg"), Contracts.CommandKind.SubmitAction, payload));
                Envelope receipt = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Rejected, receipt.Receipt.Status);

                // A valid hand tile but a decision id that is not the open authoritative one.
                byte[] wrongDecision = TableWire.SubmitActionPayload(
                    snapshot.RoundId,
                    "room-neg-fabricated-decision",
                    decision.ControlEpoch,
                    (uint)ProtocolActionType.DiscardTile,
                    new uint[] { ownTile });
                await client.SendAsync(TableWire.Command(
                    client.Welcome!, TableWire.RandomId("neg2"), Contracts.CommandKind.SubmitAction, wrongDecision));
                Envelope wrong = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Rejected, wrong.Receipt.Status);

                // Zero side effects on the authoritative chain: the fresh admission's baseline
                // still carries the base version (the refusals never entered the actor).
                await using TableTestClient observer = await host.ConnectPlayerAsync(
                    "room-neg", "acct-neg-observer", 1);
                Contracts.Wire.PlayerSnapshot after = await ReadPlayerSnapshotAsync(
                    observer,
                    predicate: s => s.PrivateView != null);
                Assert.Equal(stateVersion, after.StateVersion);
                await observer.DisposeAsync();
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        [Fact]
        public async Task CrossRoomFabrication_NeverEntersTheActor()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-fence2", "acct-fence2", 0);

            try
            {
                Contracts.Wire.PlayerSnapshot snapshot =
                    await ReadPlayerSnapshotAsync(client, predicate: s => s.PrivateView?.Decision != null);
                Contracts.Wire.DecisionView decision = RequireDecision(snapshot, seat: 0);

                // Cross-room fabrication: a command that claims a room the connection was
                // never admitted to is refused at the envelope fence (never applied).
                byte[] crossRoom = TableWire.SubmitActionPayload(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    (uint)ProtocolActionType.DiscardTile,
                    new uint[] { decision.Actions[0].TileIds[0] });
                Envelope envelope = TableWire.Command(
                    client.Welcome!, TableWire.RandomId("cross"),
                    Contracts.CommandKind.SubmitAction, crossRoom);
                envelope.RoomId = "room-fence999";
                await client.SendAsync(envelope);
                Envelope refused = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Error ||
                                (envelope.MessageType == MessageType.Receipt &&
                                 envelope.Receipt.Status == Contracts.Wire.ReceiptStatus.Rejected),
                    TimeSpan.FromSeconds(20));
                Assert.True(
                    refused.MessageType == MessageType.Error ||
                        refused.Receipt.Status == Contracts.Wire.ReceiptStatus.Rejected,
                    "a cross-room fabrication must never be applied");
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        private static Contracts.Wire.DecisionView RequireDecision(Contracts.Wire.PlayerSnapshot snapshot, int seat)
        {
            Contracts.Wire.PlayerPrivateView? view = snapshot.PrivateView;
            Assert.True(view != null, "a player snapshot always carries the owner's own private view");
            Assert.Equal(seat, view!.Seat);
            Assert.True(view.Decision != null, "the acting seat must see its open authoritative decision");
            return view.Decision!;
        }

        private static uint TileOutOfRange(Contracts.Wire.PlayerSnapshot snapshot, uint except)
        {
            var unavailable = new HashSet<uint>(snapshot.PrivateView!.ClosedTiles)
            {
                except
            };
            for (uint tile = 0; tile < 136; tile++)
            {
                if (!unavailable.Contains(tile))
                {
                    return tile;
                }
            }

            throw new Xunit.Sdk.XunitException("no alternate tile id exists");
        }

        /// <summary>
        /// Counts every physical tile identity the owner projection publishes (closed hand,
        /// own + other public melds, every seat's river). This is a conservation sanity check
        /// against the 136-tile closed world, NOT a full ownership proof (the full check is
        /// the Domain map's own ConservationException).
        /// </summary>
        private static int TilePopulationTotal(Contracts.Wire.PlayerPrivateView privateView, Contracts.Wire.PublicMatchView publicView)
        {
            var seen = new HashSet<uint>();
            void Track(uint tile)
            {
                if (tile <= 135)
                {
                    seen.Add(tile);
                }
            }

            foreach (uint tile in privateView.ClosedTiles)
            {
                Track(tile);
            }

            foreach (Contracts.Wire.PlayerPublicView player in publicView.Board.Players)
            {
                foreach (Contracts.Wire.MeldView meld in player.Melds)
                {
                    foreach (uint tile in meld.TileIds)
                    {
                        Track(tile);
                    }
                }

                foreach (Contracts.Wire.DiscardView discard in player.Discards)
                {
                    Track(discard.TileId);
                }
            }

            return seen.Count;
        }

        private static async Task<Contracts.Wire.PlayerSnapshot> ReadPlayerSnapshotAsync(
            TableTestClient client,
            Func<Contracts.Wire.PlayerSnapshot, bool>? predicate = null,
            TimeSpan? timeout = null)
        {
            TimeSpan budget = timeout ?? TimeSpan.FromSeconds(20);
            long deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
            while (true)
            {
                long remainingMs = Math.Max(1, deadline - Environment.TickCount64);
                Envelope envelope = await client.ReadUntilAsync(
                    item => item.MessageType == MessageType.PlayerUpdate &&
                            item.PlayerUpdate.Snapshot.PrivateView != null &&
                            (predicate == null || predicate(item.PlayerUpdate.Snapshot)),
                    TimeSpan.FromMilliseconds(remainingMs));
                return envelope.PlayerUpdate.Snapshot;
            }
        }
    }
}
