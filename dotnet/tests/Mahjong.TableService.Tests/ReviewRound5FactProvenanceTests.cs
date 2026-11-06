using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Mahjong.TableService;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// Round-5 review of D-1-01 against the real Kestrel host, the real actor runtime and the
    /// real trusted decision runtime: (a) a meld (pon) discard round carries PROVABLE
    /// tsumogiri facts — the hand-tile discard after a pon is false, never a cached or
    /// thrown-away fact; (b) candidate captures are ISOLATED — evaluating one discard
    /// candidate can never poison a different candidate at the same fenced state version,
    /// and a commit-failed submission leaves zero permanent advancement (the resubmit of the
    /// legal candidate still applies with the true facts); (c) chi eligibility is bound to
    /// the target's own suit at every rank boundary (1/2/8/9 × three suits, cross-suit rank-9
    /// aliases refused) and an open hand may still closed-kan a fully concealed set (the
    /// Domain's PrepareClosedKan has no menzen requirement — the service advertises and
    /// applies it); (d) the turn number is 1 before a seat's first discard and +1 per new
    /// draw afterwards — the deal-extra opening discard is turn 1 and the post-draw discard
    /// is turn 2, consistent across two full rotations.
    /// </summary>
    public sealed class ReviewRound5FactProvenanceTests
    {
        // Tile id space of the 136-tile set: type = id / 4; man1..9 = types 0..8,
        // sou1..9 = types 9..17, pin1..9 = types 18..26, honours from type 27 (East).
        private static TileId T(uint id) => new TileId((int)id);

        private const uint ManRank = 0;   // suitTile(rank r) = (suitBase + r - 1) * 4
        private const uint SouRank = 9;
        private const uint PinRank = 18;
        private const uint EastId = 108;

        /// <summary>Suit-neutral honour filler: never equals a suit target, never forms a
        /// chi, and never pons a suit discard (South/West/Haku; East excluded because it is
        /// the rank-9 alias probe tile).</summary>
        private static TileId[] HonourFiller()
        {
            return new uint[] { 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122 }
                .Select(id => T((uint)id)).ToArray();
        }

        private static TileId SuitTile(uint suitRankOffset, uint rank) => T((suitRankOffset + rank - 1) * 4);

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

        /// <summary>Claim-fit-free baseline hands for the seats that must just discard
        /// (the round-3 riichi-loop scheme: disjoint suits per seat, no pair fits). Seat 1
        /// is left to the individual scenario (it carries the meld tiles there).</summary>
        private static void SeatNoFitHands(N1aTable fixture, uint drawnForSeat0)
        {
            // Seat 0: man 1..9, pin1 x2, sou5, sou6 (+ the deal-extra drawn tile).
            var seat0 = new List<TileId>();
            for (uint tile = 0; tile <= 8; tile++)
            {
                seat0.Add(T(tile));
            }

            seat0.AddRange(new[] { T(72), T(73), T(52), T(53) });
            fixture.Hand(0, seat0.ToArray());
            fixture.Drawn(0, T(drawnForSeat0));
            fixture.Hand(2, HandRanks(new uint[] { 44, 56, 68 }));
            fixture.Hand(3, HandRanks(new uint[] { 76, 88, 100 }));
        }

        /// <summary>Seat 1's no-claim-fit sou hand with room for exactly two meld tiles
        /// (sou1 x4, sou4 x4, sou7 x3 = 11 tiles + the white pair = 13).</summary>
        private static void Seat1SouHand(N1aTable fixture)
        {
            var sou = new List<TileId>();
            for (uint copy = 0; copy < 4; copy++)
            {
                sou.Add(T(36 + copy));
                sou.Add(T(48 + copy));
            }

            sou.AddRange(new[] { T(60), T(61), T(62) });
            fixture.Hand(1, sou.ToArray());
        }

        // ======================================================================
        // (a) + (d): the pon path through the real host — the meld discard is a
        // PROVABLE false, and the turn numbers run 1, 2 across two full rotations.
        // ======================================================================

        [Fact]
        public async Task PonThenHandTileDiscard_TsumogiriProvableFalse_TurnsOneThenTwo_TwoRotations()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(roomId =>
                {
                    var fixture = new N1aTable { Dealer = 0 };
                    SeatNoFitHands(fixture, drawnForSeat0: 127);
                    // Seat 1 pons the white 127 (11 sou tiles + the pair = 13).
                    Seat1SouHand(fixture);
                    fixture.Hand(1, T(124), T(125));
                    return fixture.Build(roomId + "-r1");
                }));
            var clients = new List<TableTestClient>();
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    clients.Add(await host.ConnectPlayerAsync("room-pon5", "acct-pon5-" + seat, seat));
                }

                // Seat 0's opening discard is the DEAL-EXTRA drawn tile: tsumogiri TRUE, turn 1.
                PlayerSnapshot opening = await WaitForTurnDecisionAsync(clients[0], 0);
                Envelope discard = await SubmitAndReadAsync(
                    clients[0], opening, ProtocolActionType.DiscardTile, new uint[] { 127 });
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, discard.Receipt.Status);

                PlayerSnapshot afterOpening = await WaitForPlayerSnapshotAsync(
                    clients[1],
                    item => item.PlayerUpdate.Snapshot.PublicView!.Board.Players[0].Discards.Count == 1);
                Contracts.Wire.DiscardView openingDiscard =
                    afterOpening.PublicView!.Board.Players[0].Discards[0];
                Assert.True(openingDiscard.Tsumogiri, "the deal-extra drawn tile discard is a tsumogiri");
                Assert.Equal(1u, openingDiscard.TurnNumber);

                // The trusted window and the executable pon (the round-3 shape).
                PlayerSnapshot window = await WaitForWindowAsync(clients[1], ProtocolActionType.DeclarePon);
                Envelope pon = await SubmitAndReadAsync(
                    clients[1], window, ProtocolActionType.DeclarePon, new uint[] { 127, 124, 125 });
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, pon.Receipt.Status);

                // The meld seat's discard turn: the first hand tile is NOT the rinshan
                // tile — the river fact must be the PROVABLE false, and the turn number
                // is still 1 (a meld never advances the turn counter on its own).
                PlayerSnapshot meldTurn = await WaitForDiscardTurnAsync(clients[1], 1);
                uint meldDiscardTile = meldTurn.PrivateView!.Decision!.Actions
                    .First(action => action.ActionType == (uint)ProtocolActionType.DiscardTile)
                    .TileIds[0];
                Assert.NotEqual(124u, meldDiscardTile);
                Assert.NotEqual(125u, meldDiscardTile);
                Envelope meldDiscard = await SubmitAndReadAsync(
                    clients[1], meldTurn, ProtocolActionType.DiscardTile, new[] { meldDiscardTile });
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, meldDiscard.Receipt.Status);

                PlayerSnapshot afterMeldDiscard = await WaitForPlayerSnapshotAsync(
                    clients[2],
                    item => item.PlayerUpdate.Snapshot.PublicView!.Board.Players[1].Discards.Count == 1);
                Contracts.Wire.DiscardView meldDiscardView =
                    afterMeldDiscard.PublicView!.Board.Players[1].Discards[0];
                Assert.False(
                    meldDiscardView.Tsumogiri,
                    "the post-pon hand-tile discard is provably NOT a tsumogiri (round 5 D-1-01 a)");
                Assert.Equal(1u, meldDiscardView.TurnNumber);

                // Two full rotations IN TURN ORDER starting at seat 2 (the meld seat keeps
                // the turn). The wire turn is the GLOBAL go-around counter — 1 before the
                // round's first discard, +1 per new draw after it — so the meld discard
                // stays turn 1 and seat 2's post-meld draw opens turn 2; every discard
                // here is a HAND tile (never the seat's current drawn tile), so each one
                // stays a proven tsumogiri false. The river wait is RELATIVE: seat 1's
                // pon physically claims seat 0's opening discard out of the river (the
                // claimed tile joins the meld), so absolute pile sizes are not monotonic.
                for (int rotation = 0; rotation < 2; rotation++)
                {
                    for (int position = 0; position < 4; position++)
                    {
                        int seat = (2 + position) % 4;
                        PlayerSnapshot turn = await WaitForDiscardTurnAsync(clients[seat], seat);
                        int before = turn.PublicView!.Board.Players[seat].Discards.Count;
                        uint chosen = turn.PrivateView!.Decision!.Actions
                            .First(action => action.ActionType == (uint)ProtocolActionType.DiscardTile &&
                                             action.TileIds[0] != turn.PrivateView!.DrawnTileId)
                            .TileIds[0];
                        Envelope receipt = await SubmitAndReadAsync(
                            clients[seat], turn, ProtocolActionType.DiscardTile, new[] { chosen });
                        Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, receipt.Receipt.Status);

                        uint expectedTurn = (uint)(2 + rotation * 4 + position);
                        int next = (seat + 1) % 4;
                        PlayerSnapshot river = await WaitForPlayerSnapshotAsync(
                            clients[next],
                            item => item.PlayerUpdate.Snapshot.PublicView!.Board.Players[seat].Discards.Count ==
                                    before + 1);
                        Contracts.Wire.DiscardView view =
                            river.PublicView!.Board.Players[seat].Discards[before];
                        Assert.True(
                            view.TurnNumber == expectedTurn,
                            "seat " + seat + " rotation " + rotation +
                            " hand discard must be turn " + expectedTurn + " but was " + view.TurnNumber);
                        Assert.False(
                            view.Tsumogiri,
                            "rotation discards are hand tiles (never the seat's drawn tile)");
                    }
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

        // ======================================================================
        // (b): commit-fail-then-resubmit through the real host — the rejected
        // candidate leaves the chain untouched and its captured facts cannot
        // poison the resubmitted candidate at the same fenced version.
        // ======================================================================

        [Fact]
        public async Task CommitFailThenResubmit_NoPermanentAdvancement_NoFactPoisoning()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(RiichiLoopRound));
            var clients = new List<TableTestClient>();
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    clients.Add(await host.ConnectPlayerAsync("room-poison", "acct-pois-" + seat, seat));
                }

                PlayerSnapshot first = await WaitForTurnDecisionAsync(clients[0], 0);
                Envelope riichi = await SubmitAndReadAsync(
                    clients[0], first, ProtocolActionType.DeclareRiichi, new uint[] { 80 });
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, riichi.Receipt.Status);

                for (int seat = 1; seat <= 3; seat++)
                {
                    PlayerSnapshot turn = await WaitForTurnDecisionAsync(clients[seat], seat);
                    uint chosen = turn.PrivateView!.Decision!.Actions
                        .First(action => action.ActionType == (uint)ProtocolActionType.DiscardTile)
                        .TileIds[0];
                    Envelope discard = await SubmitAndReadAsync(
                        clients[seat], turn, ProtocolActionType.DiscardTile, new[] { chosen });
                    Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, discard.Receipt.Status);
                }

                PlayerSnapshot locked = await WaitForTurnDecisionAsync(clients[0], 0);
                uint lockedTile = locked.PrivateView!.Decision!.Actions[0].TileIds[0];

                // Candidate A: a genuinely held FOREIGN hand tile. The Domain refuses at
                // commit — the capture that evaluated it derived a hand-tile (non-tsumogiri)
                // fact at exactly this fenced version, and the chain does not move.
                uint foreign = locked.PrivateView!.ClosedTiles.First(tile => tile != lockedTile);
                byte[] payload = TableWire.SubmitActionPayload(
                    locked.RoundId, locked.PrivateView!.Decision!.DecisionId,
                    locked.PrivateView!.Decision!.ControlEpoch,
                    (uint)ProtocolActionType.DiscardTile, new[] { foreign });
                string requestId = TableWire.RandomId("r5b");
                await clients[0].SendAsync(TableWire.Command(
                    clients[0].Welcome!, requestId,
                    Contracts.CommandKind.SubmitAction, payload,
                    expectedStateVersion: locked.StateVersion));
                Envelope refused = await clients[0].ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt &&
                                envelope.RequestId == requestId,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Rejected, refused.Receipt.Status);
                Assert.True(
                    locked.StateVersion == refused.Receipt.EvaluatedStateVersion,
                    "the rejected candidate leaves zero permanent advancement");

                // Candidate B at the SAME fenced version: the locked drawn tile. Its
                // capture must derive its OWN facts — the resubmit applies and the river
                // carries the true tsumogiri/riichi facts of the DRAWN tile (never the
                // foreign candidate's cached non-tsumogiri fact).
                Envelope ok = await SubmitAndReadAsync(
                    clients[0], locked, ProtocolActionType.DiscardTile, new[] { lockedTile });
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, ok.Receipt.Status);
                Assert.True(ok.Receipt.CommandVersion > refused.Receipt.EvaluatedStateVersion);

                PlayerSnapshot after = await WaitForPlayerSnapshotAsync(
                    clients[1],
                    item => item.PlayerUpdate.Snapshot.PublicView!.Board.Players[0].Discards.Count == 2);
                Contracts.Wire.DiscardView view = after.PublicView!.Board.Players[0].Discards[1];
                // The resubmitted candidate is the seat's CURRENT drawn tile (the Domain's
                // riichi lock is tsumogiri-only; the drawn tile advances every rotation) —
                // the exact tile that was submitted is what the river must carry.
                Assert.Equal(lockedTile, view.TileId);
                Assert.True(
                    view.Tsumogiri,
                    "the resubmitted locked discard carries its OWN true tsumogiri fact " +
                    "(no carry-over from the rejected candidate's capture)");
                Assert.True(
                    view.TurnNumber == 5u,
                    "the locked discard is turn 5 on the GLOBAL go-around counter (the riichi " +
                    "discard is turn 1; seats 1..3 each draw once -> turns 2..4; seat 0's next " +
                    "draw opens turn 5) but was " + view.TurnNumber);
            }
            finally
            {
                foreach (TableTestClient client in clients)
                {
                    await client.DisposeAsync();
                }
            }
        }

        /// <summary>The exact reviewer probe shape at the capture boundary: ONE projection
        /// context source, two candidate captures at the SAME fenced state version with
        /// DIFFERENT discarded tiles — each capture derives its own facts from its own
        /// round events; the second capture never inherits the first's tsumogiri fact.</summary>
        [Fact]
        public void CaptureIsolation_SameVersionDifferentCandidates_DeriveIndependently()
        {
            // The shared fence: version 1, two events (the discard + the auto-advance draw),
            // identical fixture shape — candidate A discards the drawn tile 127, candidate B
            // discards the hand tile man1.
            RoundStateMachine fixtureA = PonFixtureRound("room-iso-a");
            RoundStateMachine fixtureB = PonFixtureRound("room-iso-b");
            var source = new TableProjectionContextSource(decisionState: null, initialRound: fixtureA);

            (RoundStateMachine Round, List<TableEventRecord> Records) a = Advance(fixtureA, 127);
            (RoundStateMachine Round, List<TableEventRecord> Records) b = Advance(fixtureB, 0);

            TableProjectionContextData factsA = source.Capture(CaptureRequest("room-iso", a.Round, a.Records));
            TableProjectionContextData factsB = source.Capture(CaptureRequest("room-iso", b.Round, b.Records));

            Contracts.DiscardView viewA = factsA.PublicFacts[0].Discards[0];
            Contracts.DiscardView viewB = factsB.PublicFacts[0].Discards[0];

            Assert.True(viewA.Tsumogiri, "candidate A discards the deal-extra drawn tile 127");
            Assert.False(
                viewB.Tsumogiri,
                "candidate B discards the hand tile man1 at the SAME fenced version — the " +
                "candidate A capture must not poison it (round 5 D-1-01 b probe flip)");
            Assert.Equal(1u, viewA.TurnNumber);
            Assert.Equal(1u, viewB.TurnNumber);
        }

        // ======================================================================
        // (c) chi: the trusted eligibility is suit-bound at every rank boundary.
        // ======================================================================

        /// <summary>Ranks 1/2/8/9 × three suits: the legal pairing is advertised, and the
        /// cross-suit rank-9 alias pairing (previous suit's 9 / next zone's 1) is NEVER
        /// eligible — the exact reviewer probe shape, derived by the REAL trusted
        /// response-window eligibility through NoteCapture.</summary>
        [Fact]
        public void ChiEligibility_RankBoundaries_ThreeSuits_CrossSuitAliasNeverEligible()
        {
            var cases = new List<(string Name, uint Suit, uint Rank, TileId[] Pair, bool ExpectEligible)>();
            foreach (uint suit in new[] { ManRank, SouRank, PinRank })
            {
                string suitName = suit == ManRank ? "man" : suit == SouRank ? "sou" : "pin";
                // The type-space neighbours the alias pairs exploit: the tile BEFORE a
                // suit's rank 1 is the previous suit's rank 9 (sou9 for pin1, man9 for
                // sou1), and the tile AFTER a suit's rank 9 is the next zone's rank 1
                // (sou1 after man9, pin1 after sou9, East after pin9).
                TileId prevSuit9 = suit switch
                {
                    SouRank => SuitTile(ManRank, 9),
                    PinRank => SuitTile(SouRank, 9),
                    _ => SuitTile(SouRank, 9) // man has no previous suit; any foreign 9 is a negative
                };
                TileId nextZone1 = suit switch
                {
                    ManRank => SuitTile(SouRank, 1),
                    SouRank => SuitTile(PinRank, 1),
                    _ => T(EastId)
                };
                foreach (uint rank in new uint[] { 1, 2, 8, 9 })
                {
                    TileId[] positive = rank switch
                    {
                        1 => new[] { SuitTile(suit, 2), SuitTile(suit, 3) },   // (r+1, r+2)
                        2 => new[] { SuitTile(suit, 1), SuitTile(suit, 3) },   // (r-1, r+1)
                        8 => new[] { SuitTile(suit, 7), SuitTile(suit, 9) },   // (r-1, r+1)
                        _ => new[] { SuitTile(suit, 7), SuitTile(suit, 8) }    // (r-2, r-1)
                    };
                    TileId[] alias = rank switch
                    {
                        // rank-1: the previous suit's 9 pairs with the target's 2 — the
                        // reviewer's exact cross-suit probe.
                        1 => new[] { prevSuit9, SuitTile(suit, 2) },
                        // rank-2: the previous suit's 9 pairs with the target's 1.
                        2 => new[] { prevSuit9, SuitTile(suit, 1) },
                        // rank-8: the target's 9 pairs with the next zone's 1.
                        8 => new[] { SuitTile(suit, 9), nextZone1 },
                        // rank-9: the target's 8 pairs with the next zone's 1.
                        _ => new[] { SuitTile(suit, 8), nextZone1 }
                    };
                    cases.Add((suitName + rank + "-legal-pair", suit, rank, positive, true));
                    cases.Add((suitName + rank + "-cross-suit-alias", suit, rank, alias, false));
                }
            }

            foreach ((string name, uint suit, uint rank, TileId[] pair, bool expectEligible) in cases)
            {
                TileId target = SuitTile(suit, rank);
                var fixture = new N1aTable { Dealer = 0 };
                fixture.Drawn(0, target);
                var hand = new List<TileId>(pair);
                hand.AddRange(HonourFiller());
                fixture.Hand(1, hand.ToArray());
                RoundStateMachine round = fixture.Build("room-chi-" + name);

                var state = new TableRoomDecisionState();
                GetInternal(state, "NoteCapture").Invoke(
                    state, new object[] { "room-chi-" + name, round, 0UL, 100L, null! });
                var eligibility = Assert.IsType<TableCommandRouteResponseEligibility>(
                    GetInternal(state, "DeriveResponseEligibility")
                        .Invoke(state, new object[] { 0, target, 0UL }));
                Assert.NotNull(eligibility);
                bool isEligible = eligibility!.EligibleSeats.Contains(1);
                Assert.True(
                    isEligible == expectEligible,
                    "chi case " + name + " (target id " + target.Value + ", pair [" +
                    string.Join(",", pair.Select(tile => tile.Value)) + "]): expected " +
                    (expectEligible ? "ELIGIBLE" : "NOT eligible") + " but got " +
                    (isEligible ? "eligible" : "not eligible"));
            }
        }

        // ======================================================================
        // (c) closed kan: an OPEN hand may still closed-kan a fully concealed set.
        // ======================================================================

        [Fact]
        public async Task OpenHand_ClosedKan_CandidateAdvertised_AndAppliesThroughTheHost()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                o => { o.RoomTickIntervalMs = 50; },
                roundSource: new FixedRoundSource(roomId =>
                {
                    var fixture = new N1aTable { Dealer = 0 };
                    SeatNoFitHands(fixture, drawnForSeat0: 127);
                    // Seat 1: the white pair (for the pon that OPENS the hand) plus the
                    // fully concealed East set — the reviewer's exact probe hand shape.
                    fixture.Hand(
                        1,
                        T(124), T(125),
                        T(EastId), T(EastId + 1), T(EastId + 2), T(EastId + 3),
                        T(36), T(37), T(48), T(49), T(60), T(61), T(62));
                    return fixture.Build(roomId + "-r1");
                }));
            var clients = new List<TableTestClient>();
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    clients.Add(await host.ConnectPlayerAsync("room-kan5", "acct-kan5-" + seat, seat));
                }

                PlayerSnapshot opening = await WaitForTurnDecisionAsync(clients[0], 0);
                Envelope discard = await SubmitAndReadAsync(
                    clients[0], opening, ProtocolActionType.DiscardTile, new uint[] { 127 });
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, discard.Receipt.Status);

                PlayerSnapshot window = await WaitForWindowAsync(clients[1], ProtocolActionType.DeclarePon);
                Envelope pon = await SubmitAndReadAsync(
                    clients[1], window, ProtocolActionType.DeclarePon, new uint[] { 127, 124, 125 });
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, pon.Receipt.Status);

                // The OPEN hand's discard turn: the closed-kan candidate MUST be
                // advertised (the round-4 service masked it behind a menzen test the
                // Domain does not have).
                PlayerSnapshot meldTurn = await WaitForDiscardTurnAsync(clients[1], 1);
                Contracts.Wire.DecisionView decision = meldTurn.PrivateView!.Decision!;
                Contracts.Wire.LegalActionView kan = Assert.Single(
                    decision.Actions,
                    action => action.ActionType == (uint)ProtocolActionType.DeclareKan);
                Assert.Equal(EastId, kan.TileIds[0]);

                byte[] kanPayload = KanPayload(
                    meldTurn.RoundId, decision.DecisionId, decision.ControlEpoch,
                    new[] { EastId, EastId + 1, EastId + 2, EastId + 3 });
                string kanRequest = TableWire.RandomId("r5kan");
                await clients[1].SendAsync(TableWire.Command(
                    clients[1].Welcome!, kanRequest,
                    Contracts.CommandKind.SubmitAction, kanPayload,
                    expectedStateVersion: meldTurn.StateVersion));
                Envelope kanReceipt = await clients[1].ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt &&
                                envelope.RequestId == kanRequest,
                    TimeSpan.FromSeconds(20));
                Assert.True(
                    kanReceipt.Receipt.Status == Contracts.Wire.ReceiptStatus.Applied,
                    "the open-hand closed kan must apply through the Domain's PrepareClosedKan " +
                    "but was " + kanReceipt.Receipt.Status + " (" + kanReceipt.Receipt.ReasonCode + ")");
            }
            finally
            {
                foreach (TableTestClient client in clients)
                {
                    await client.DisposeAsync();
                }
            }
        }

        /// <summary>The Domain accepts a closed kan from an OPEN hand and the service
        /// advertises it; what the frozen TableRuntime event encoder does afterwards is a
        /// platform boundary outside this task's diff scope: TableEventProjection never maps
        /// the Domain's CLOSED_KAN detail into its closed-kan flag, so the KanDeclared event
        /// frame cannot satisfy the wire's owner-only identity redaction and the fail-safe
        /// encoder closes the streams. This second path proves the DOMAIN acceptance in
        /// isolation — pon, then closed kan, then the rinshan discard — with no stream
        /// riding on the kan event frame.</summary>
        [Fact]
        public void ClosedKan_TransitionChain_PonThenClosedKanThenRinshanDiscard()
        {
            var fixture = new N1aTable { Dealer = 0 };
            fixture.Drawn(0, T(127));
            fixture.Hand(
                1,
                T(124), T(125),
                T(EastId), T(EastId + 1), T(EastId + 2), T(EastId + 3),
                T(36), T(37), T(48), T(49), T(60), T(61), T(62));
            RoundStateMachine round = fixture.Build("room-kan-chain");
            RoundStateMachine initialRound = round;
            var source = new TableProjectionContextSource(initialRound: initialRound);

            // Seat 1 must be the sole eligible claimant of the opening discard: without
            // explicit eligibility the Domain resolves the claim window inside the same
            // transition (auto-draw) and the pon would have nothing to claim.
            RoundTransition discard = round.Execute(
                RoundCommand.Discard(round.RoundId, 0, T(127), new[] { 1 }));
            Assert.True(discard.IsAccepted, "the opening discard must be legal");
            round = discard.State!;

            RoundTransition pon = round.Execute(RoundCommand.Pon(round.RoundId, 1, T(127), T(124), T(125)));
            Assert.True(pon.IsAccepted, "the pon must be legal: " + pon.Error?.Message);
            round = pon.State!;
            Assert.Single(round.Melds, meld => meld.Seat == 1 && meld.Kind == RoundMeldKind.Pon);

            RoundTransition kan = round.Execute(RoundCommand.ClosedKan(
                round.RoundId, 1, T(EastId), T(EastId + 1), T(EastId + 2), T(EastId + 3)));
            Assert.True(kan.IsAccepted, "the OPEN hand's closed kan must be accepted: " + kan.Error?.Message);
            round = kan.State!;
            Assert.Single(round.Melds, meld => meld.Seat == 1 && meld.Kind == RoundMeldKind.ClosedKan);

            // The transition chain: dora reveal, the kan event, the rinshan replacement draw.
            Assert.Contains(kan.Events, roundEvent => roundEvent.Type == RoundEventType.KanDeclared);
            Assert.Contains(kan.Events, roundEvent => roundEvent.Type == RoundEventType.RinshanDrawn);
            Assert.Equal(1, round.CurrentSeat);

            // The projection captures the kan chain fail-closed cleanly: every committed
            // event keeps its honest turn fact and the closed-kan meld fact is the
            // redacted-identity owner form (four tiles, no called tile, no source seat).
            var records = new List<TableEventRecord>();
            ulong seq = 1;
            foreach (RoundEvent roundEvent in round.Events)
            {
                records.Add(new TableEventRecord(
                    new EventSeq(seq++),
                    new StateVersion((ulong)kan.CommandVersion!.Value),
                    0,
                    roundEvent.Type,
                    roundEvent.Seat,
                    roundEvent.Tile,
                    roundEvent.Detail));
            }

            var request = new TableProjectionContextRequest(
                "room-kan-chain",
                round,
                new StateVersion(1),
                new EventSeq((ulong)records.Count),
                new OwnerEpoch(1),
                controlVersion: 0,
                recoveryGeneration: 0,
                monotonicMillis: 100,
                utcMillis: 1_760_000_000_000,
                records);
            TableProjectionContextData facts = source.Capture(request);
            TableProjectionEventFact kanFact = Assert.Single(
                facts.EventFacts,
                fact => fact.Type == ProtocolEventType.KanDeclared);
            Assert.NotNull(kanFact.Facts.Meld);
            Assert.Equal(4, kanFact.Facts.Meld!.TileIds.Count);
            Assert.Equal(ProtocolMeldType.ClosedKan, kanFact.Facts.Meld.Type);
            Assert.False(kanFact.Facts.Meld.CalledTileId.HasValue);
        }

        // ------------------------------------------------------------------ helpers

        private static RoundStateMachine RiichiLoopRound(string roomId)
        {
            var fixture = new N1aTable { Dealer = 0 };
            SeatNoFitHands(fixture, drawnForSeat0: 80);
            fixture.Hand(1, HandRanks(new uint[] { 36, 48, 60 }));
            return fixture.Build(roomId + "-r1");
        }

        private static RoundStateMachine PonFixtureRound(string roomId)
        {
            var fixture = new N1aTable { Dealer = 0 };
            SeatNoFitHands(fixture, drawnForSeat0: 127);
            Seat1SouHand(fixture);
            fixture.Hand(1, T(124), T(125));
            return fixture.Build(roomId);
        }

        /// <summary>Plays one discard on a fixture round exactly the way the actor records
        /// it: the transition's events become per-event records of one command version.</summary>
        private static (RoundStateMachine Round, List<TableEventRecord> Records) Advance(
            RoundStateMachine round, uint discardedId)
        {
            RoundTransition transition = round.Execute(
                RoundCommand.Discard(round.RoundId, 0, T(discardedId)));
            Assert.True(transition.IsAccepted, "the fixture discard must be legal: " + discardedId);
            var records = new List<TableEventRecord>();
            ulong seq = 1;
            int ordinal = 0;
            foreach (RoundEvent roundEvent in transition.Events)
            {
                records.Add(new TableEventRecord(
                    new EventSeq(seq++),
                    new StateVersion((ulong)transition.CommandVersion!.Value),
                    ordinal,
                    roundEvent.Type,
                    roundEvent.Seat,
                    roundEvent.Tile,
                    roundEvent.Detail));
                ordinal++;
            }

            return (transition.State!, records);
        }

        private static TableProjectionContextRequest CaptureRequest(
            string roomId, RoundStateMachine round, List<TableEventRecord> records)
        {
            return new TableProjectionContextRequest(
                roomId,
                round,
                new StateVersion(1),
                new EventSeq((ulong)records.Count),
                new OwnerEpoch(1),
                controlVersion: 0,
                recoveryGeneration: 0,
                monotonicMillis: 100,
                utcMillis: 1_760_000_000_000,
                records);
        }

        private static MethodInfo GetInternal(object instance, string method)
        {
            return instance.GetType().GetMethod(
                    method,
                    BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("missing internal method " + method);
        }

        /// <summary>The frozen MJC1 SubmitAction payload with the closed-kan meld type.</summary>
        private static byte[] KanPayload(
            string roundId, string decisionId, ulong controlEpoch, uint[] tiles)
        {
            var writer = new CanonicalPayloadWriter((ushort)Contracts.CommandKind.SubmitAction);
            writer.WriteString(CommandPayloadFields.RoundIdField, roundId);
            writer.WriteString(CommandPayloadFields.DecisionIdField, decisionId);
            writer.WriteUInt64(CommandPayloadFields.ControlEpochField, controlEpoch);
            writer.WriteUInt32(CommandPayloadFields.ActionType, (uint)ProtocolActionType.DeclareKan);
            writer.WriteRepeatedUInt32(CommandPayloadFields.TileIds, tiles);
            writer.WriteUInt32(CommandPayloadFields.MeldType, (uint)Contracts.Wire.MeldType.ClosedKan);
            return writer.Build().CopyBytes();
        }

        /// <summary>Waits for the seat's response window (its private decision advertising
        /// the claim type). A response window keeps CurrentSeat on the DISCARDER, so a
        /// CurrentSeat check would miss the window and catch the post-timeout turn. The
        /// decision-seen guard keeps a redelivered twin of an older window from posing as
        /// this one (the publish path delivers every open decision twice).</summary>
        private static async Task<PlayerSnapshot> WaitForWindowAsync(
            TableTestClient client, ProtocolActionType claimType, int seconds = 30)
        {
            long deadline = Environment.TickCount64 + seconds * 1000L;
            while (true)
            {
                int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
                Envelope envelope = await client.ReadUntilAsync(
                    item => item.MessageType == MessageType.PlayerUpdate &&
                            item.PlayerUpdate.Snapshot.PrivateView?.Decision != null &&
                            item.PlayerUpdate.Snapshot.PrivateView.Decision.Actions.Any(
                                action => action.ActionType == (uint)claimType) &&
                            !client.IsDecisionSeen(
                                item.PlayerUpdate.Snapshot.PrivateView.Seat,
                                item.PlayerUpdate.Snapshot.PrivateView.Decision.DecisionId),
                    TimeSpan.FromMilliseconds(remaining));
                PlayerSnapshot snapshot = envelope.PlayerUpdate.Snapshot;
                client.MarkDecisionSeen(
                    snapshot.PrivateView!.Seat, snapshot.PrivateView!.Decision!.DecisionId);
                return snapshot;
            }
        }

        private static async Task<PlayerSnapshot> WaitForTurnDecisionAsync(
            TableTestClient client, int seat, int seconds = 30)
        {
            // The publish path delivers each open decision twice (delta plus full-snapshot
            // twin); a seat's NEXT-decision wait must never resurface a decision a previous
            // wait on this client already returned for the seat — that twin is the stale
            // candidate set of an older turn.
            long deadline = Environment.TickCount64 + seconds * 1000L;
            while (true)
            {
                int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
                Envelope envelope = await client.ReadUntilAsync(
                    item => item.MessageType == MessageType.PlayerUpdate &&
                            item.PlayerUpdate.Snapshot.PrivateView?.Decision != null &&
                            (int)item.PlayerUpdate.Snapshot.PublicView!.Board.CurrentSeat == seat &&
                            !client.IsDecisionSeen(
                                seat, item.PlayerUpdate.Snapshot.PrivateView.Decision.DecisionId),
                    TimeSpan.FromMilliseconds(remaining));
                PlayerSnapshot snapshot = envelope.PlayerUpdate.Snapshot;
                client.MarkDecisionSeen(seat, snapshot.PrivateView!.Decision!.DecisionId);
                return snapshot;
            }
        }

        /// <summary>Waits for the seat's DISCARD turn (a decision that advertises
        /// DiscardTile) — response windows on the same fence are passed on the way.</summary>
        private static async Task<PlayerSnapshot> WaitForDiscardTurnAsync(
            TableTestClient client, int seat, int seconds = 30)
        {
            long deadline = Environment.TickCount64 + seconds * 1000L;
            while (true)
            {
                int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
                Envelope envelope = await client.ReadUntilAsync(
                    item => item.MessageType == MessageType.PlayerUpdate &&
                            item.PlayerUpdate.Snapshot.PrivateView?.Decision != null &&
                            (int)item.PlayerUpdate.Snapshot.PublicView!.Board.CurrentSeat == seat &&
                            item.PlayerUpdate.Snapshot.PrivateView.Decision.Actions.Any(
                                action => action.ActionType == (uint)ProtocolActionType.DiscardTile) &&
                            !client.IsDecisionSeen(
                                seat, item.PlayerUpdate.Snapshot.PrivateView.Decision.DecisionId),
                    TimeSpan.FromMilliseconds(remaining));
                PlayerSnapshot snapshot = envelope.PlayerUpdate.Snapshot;
                client.MarkDecisionSeen(seat, snapshot.PrivateView!.Decision!.DecisionId);
                return snapshot;
            }
        }

        private static async Task<PlayerSnapshot> WaitForPlayerSnapshotAsync(
            TableTestClient client, Func<Envelope, bool> predicate, int seconds = 30)
        {
            long deadline = Environment.TickCount64 + seconds * 1000L;
            while (true)
            {
                int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
                Envelope envelope = await client.ReadUntilAsync(
                    predicate, TimeSpan.FromMilliseconds(remaining));
                return envelope.PlayerUpdate.Snapshot;
            }
        }

        private static async Task<Envelope> SubmitAndReadAsync(
            TableTestClient client,
            PlayerSnapshot snapshot,
            ProtocolActionType actionType,
            uint[] tileIds)
        {
            string requestId = TableWire.RandomId("r5");
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
    }
}
