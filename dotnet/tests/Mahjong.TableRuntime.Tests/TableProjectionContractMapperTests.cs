using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class TableProjectionContractMapperTests
    {
        [Fact]
        public void real_p3_2a_public_and_owner_projection_maps_to_contracts_payloads()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            round = WithSleeve(round, 0);
            round = V32Fixtures.WithMeld(round, 3, 4, ProtocolMeldType.ClosedKan, out TableMeldFact closedKan);
            CheatState state = RichCheat(round);

            TableProjectionContext context = V32Fixtures.Context(
                round,
                melds: new Dictionary<int, IReadOnlyList<TableMeldFact>>
                {
                    [3] = new[] { closedKan }
                },
                preparation: new[] { true, false, false, false });
            PublicSnapshotView publicView = TableVisibilityProjection.ProjectPublicSnapshot(round, context);
            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(round, state, 0, context);
            PlayerSnapshotView seat3 = TableVisibilityProjection.ProjectPlayerSnapshot(round, state, 3, context);
            TableOwnerPrivateView owner0 = TableVisibilityProjection.ProjectOwnerPrivate(round, state, 0, context);
            TableOwnerPrivateView owner3 = TableVisibilityProjection.ProjectOwnerPrivate(round, state, 3, context);

            PublicSnapshotView mappedPublic = TableProjectionContractMapper.MapPublicSnapshot(
                publicView,
                concealedMeldFacts: new[] { new TablePublicConcealedMeldFact(3) });
            Assert.Empty(mappedPublic.View.Board!.Players[3].Melds);
            Assert.Single(mappedPublic.View.Board.Players[3].ConcealedMelds);
            Assert.Equal(ProtocolMeldType.ClosedKan, mappedPublic.View.Board.Players[3].ConcealedMelds[0].Type);
            Assert.Equal(4u, mappedPublic.View.Board.Players[3].ConcealedMelds[0].TileCount);
            Assert.Equal(-1, mappedPublic.View.Board.Players[3].ConcealedMelds[0].FromSeat);
            Assert.Equal(publicView.Cursor, mappedPublic.Cursor);
            Assert.Equal(publicView.EventSeq, mappedPublic.EventSeq);
            Assert.Equal(publicView.StateVersion, mappedPublic.StateVersion);

            var time = new TableProjectionTimeContext(1200);
            PlayerSnapshotView mappedSeat0 = TableProjectionContractMapper.MapPlayerSnapshot(seat0, owner0, time);
            Assert.Equal(seat0.Seat, mappedSeat0.Seat);
            Assert.Equal(seat0.Cursor, mappedSeat0.Cursor);
            Assert.Equal(owner0.ClosedTiles.ToArray(), mappedSeat0.Private.ClosedTiles.ToArray());
            Assert.Equal(owner0.SleeveTiles.ToArray(), mappedSeat0.Private.SleeveTiles.ToArray());
            Assert.Equal(2, mappedSeat0.Private.SleeveTiles.Count);
            Assert.Equal(995L, mappedSeat0.Private.Cheat!.CheatPoints);
            Assert.Single(mappedSeat0.Private.Cheat.Ledger);
            Assert.Equal("ledger-0", mappedSeat0.Private.Cheat.Ledger[0].EntryId);
            Assert.NotNull(mappedSeat0.Private.Cheat.OwnerTileMark);
            Assert.Single(mappedSeat0.Private.Cheat.OwnerTileMark!.Groups);
            Assert.NotNull(mappedSeat0.Private.Cheat.ImmediateSleeveOffer);
            Assert.NotNull(mappedSeat0.Private.Cheat.OffTurnSleeveOffer);
            Assert.Equal("immediate-0", mappedSeat0.Private.Cheat.ImmediateSleeveOffer!.OfferId);
            Assert.Equal("offturn-0", mappedSeat0.Private.Cheat.OffTurnSleeveOffer!.OfferId);
            Assert.True(mappedSeat0.Private.Cheat.OffTurnSleeveOffer.HasDeadline);
            Assert.Equal(800L, mappedSeat0.Private.Cheat.OffTurnSleeveOffer.DeadlineRemainingMs);
            Assert.Equal(mappedSeat0.Private.Cheat.OffTurnSleeveOffer.WindowId, mappedSeat0.Private.Cheat.WindowId);
            Assert.NotNull(mappedSeat0.Private.Cheat.SleeveReservation);
            Assert.Equal(6u, mappedSeat0.Private.Cheat.SleeveReservation!.TileId);
            Assert.NotNull(mappedSeat0.Private.Cheat.OwnerBeamSwap);
            Assert.Single(mappedSeat0.Private.Cheat.OwnerBeamSwap!.Candidates);
            Assert.Equal(54u, mappedSeat0.Private.Cheat.OwnerBeamSwap.Candidates[0].SlotIndex);
            Assert.Equal(ProjectionBeamSwapSlotKind.Wall, mappedSeat0.Private.Cheat.OwnerBeamSwap.Candidates[0].SlotKind);
            Assert.True(mappedSeat0.Private.Cheat.HasPreparation);
            Assert.Null(mappedSeat0.Private.Cheat.Preparation);

            PlayerSnapshotView mappedSeat3 = TableProjectionContractMapper.MapPlayerSnapshot(
                seat3,
                owner3,
                time,
                concealedMeldFacts: new[] { new TablePublicConcealedMeldFact(3) });
            Assert.Single(mappedSeat3.Private.OwnerMelds);
            Assert.Equal(ProtocolMeldType.ClosedKan, mappedSeat3.Private.OwnerMelds[0].Type);
            Assert.Equal(4, mappedSeat3.Private.OwnerMelds[0].TileIds.Count);
            Assert.Single(mappedSeat3.Public.Board!.Players[3].ConcealedMelds);
            Assert.Empty(mappedSeat3.Public.Board.Players[3].Melds);
        }

        [Fact]
        public void mapper_rejects_missing_required_event_facts_and_preserves_projected_identity()
        {
            TableProjectedEvent discard = TableEventProjection.Project(
                V32Fixtures.Event(
                    ProtocolEventType.TileDiscarded,
                    0,
                    new uint[] { 5 },
                    seq: 9,
                    commandVersion: 4,
                    ordinal: 2),
                TableProjectionAudience.ForSeat(0),
                new TableProjectionPolicy())!;

            Assert.Throws<TableProjectionException>(() =>
                TableProjectionContractMapper.MapEvent(discard, facts: null));

            ProtocolEventRecord mapped = TableProjectionContractMapper.MapEvent(
                discard,
                new TableProjectionEventFacts(turnNumber: 7, riichiDiscard: false, tsumogiri: false));
            Assert.Equal(ProtocolEventType.TileDiscarded, mapped.Type);
            Assert.Equal(new EventSeq(9), mapped.EventSeq);
            Assert.Equal(new StateVersion(4), mapped.CommandVersion);
            Assert.Equal(2u, mapped.EventOrdinal);
            Assert.Equal(ProtocolEventVisibility.Public, mapped.Visibility);
            Assert.NotNull(mapped.ProjectionPayload);
            Assert.Equal(new uint[] { 5 }, mapped.ProjectionPayload!.TileIds.ToArray());

            TableProjectedEvent draw = TableEventProjection.Project(
                V32Fixtures.Event(ProtocolEventType.DrawGame, reason: "EXHAUSTIVE_DRAW", seq: 10, commandVersion: 5),
                TableProjectionAudience.Spectator,
                new TableProjectionPolicy())!;
            Assert.Throws<TableProjectionException>(() =>
                TableProjectionContractMapper.MapEvent(draw, new TableProjectionEventFacts(turnNumber: 8)));
            ProtocolEventRecord drawMapped = TableProjectionContractMapper.MapEvent(
                draw,
                new TableProjectionEventFacts(turnNumber: 8, tenpaiPlayers: new[] { 0, 2 }));
            Assert.Equal(new[] { 0, 2 }, drawMapped.ProjectionPayload!.TenpaiPlayers.ToArray());

            TableProjectedEvent cheat = TableEventProjection.Project(
                V32Fixtures.Event(
                    ProtocolEventType.CheatPointsChanged,
                    0,
                    seq: 11,
                    commandVersion: 6,
                    cheatAmount: -5,
                    cheatBalance: 995),
                TableProjectionAudience.ForSeat(0),
                new TableProjectionPolicy())!;
            ProtocolEventRecord cheatMapped = TableProjectionContractMapper.MapEvent(
                cheat,
                new TableProjectionEventFacts(turnNumber: 9));
            Assert.Equal(-5L, cheatMapped.ProjectionPayload!.CheatAmount);
            Assert.Equal(995L, cheatMapped.ProjectionPayload.CheatBalance);
        }

        [Fact]
        public void public_and_owner_closed_kan_events_use_the_right_redaction_boundary()
        {
            TableProjectedEvent publicKan = TableEventProjection.Project(
                V32Fixtures.Event(
                    ProtocolEventType.KanDeclared,
                    3,
                    new uint[] { 24, 25, 26, 27 },
                    seq: 12,
                    commandVersion: 7,
                    closedKan: true),
                TableProjectionAudience.Spectator,
                new TableProjectionPolicy())!;
            Assert.Empty(publicKan.TileIds);

            var meld = new MeldView(
                ProtocolMeldType.ClosedKan,
                new uint[] { 24, 25, 26, 27 },
                24,
                ProtocolConstants.NoSeat);
            Assert.Throws<TableProjectionException>(() =>
                TableProjectionContractMapper.MapEvent(
                    publicKan,
                    new TableProjectionEventFacts(turnNumber: 10, meld: meld)));

            ProtocolEventRecord publicMapped = TableProjectionContractMapper.MapEvent(
                publicKan,
                new TableProjectionEventFacts(turnNumber: 10));
            Assert.Null(publicMapped.ProjectionPayload!.Meld);
            Assert.Equal(ProtocolMeldType.ClosedKan, publicMapped.ProjectionPayload.KanType);

            TableProjectedEvent ownerKan = TableEventProjection.Project(
                V32Fixtures.Event(
                    ProtocolEventType.KanDeclared,
                    3,
                    new uint[] { 24, 25, 26, 27 },
                    seq: 13,
                    commandVersion: 8,
                    closedKan: true),
                TableProjectionAudience.ForSeat(3),
                new TableProjectionPolicy())!;
            Assert.Equal(4, ownerKan.TileIds.Count);
            ProtocolEventRecord ownerMapped = TableProjectionContractMapper.MapEvent(
                ownerKan,
                new TableProjectionEventFacts(turnNumber: 10, meld: meld));
            Assert.NotNull(ownerMapped.ProjectionPayload!.Meld);
            Assert.Equal(4, ownerMapped.ProjectionPayload.Meld!.TileIds.Count);
        }

        [Fact]
        public void mapper_preserves_match_and_round_session_lifecycle()
        {
            long[] finalScores = { 30000, 25000, 24000, 21000 };
            RoundStateMachine finishedRound = V32Fixtures.ApplyTsumo(
                P31Fixtures.Dealt("round-1"),
                0,
                finalScores);
            var session = new TableSessionProjection(
                true,
                "MATCH_END",
                finalScores,
                new[] { 0, 1, 2, 3 },
                "round-1");
            TableProjectionContext context = V32Fixtures.Context(finishedRound, session: session);
            PublicSnapshotView publicView = TableVisibilityProjection.ProjectPublicSnapshot(finishedRound, context);
            Assert.True(publicView.View.Session.Finished);
            PublicSnapshotView mapped = TableProjectionContractMapper.MapPublicSnapshot(publicView);
            Assert.True(mapped.View.Session.Finished);
            Assert.Equal("MATCH_END", mapped.View.Session.FinishReason);
            Assert.Equal(4, mapped.View.Session.FinalScores.Count);
            Assert.Equal(new[] { 0, 1, 2, 3 }, mapped.View.Session.Rankings.ToArray());
            Assert.Equal("round-1", mapped.View.SettlementRoundId);

            TableProjectionContext neutralContext = V32Fixtures.Context(P31Fixtures.Dealt("round-2"));
            PublicSnapshotView neutral = TableVisibilityProjection.ProjectPublicSnapshot(
                P31Fixtures.Dealt("round-2"),
                neutralContext);
            PublicSnapshotView mappedNeutral = TableProjectionContractMapper.MapPublicSnapshot(neutral);
            Assert.False(mappedNeutral.View.Session.Finished);
            Assert.Empty(mappedNeutral.View.Session.FinalScores);
            Assert.Empty(mappedNeutral.View.Session.Rankings);
        }

        [Fact]
        public void mapper_rejects_owner_or_snapshot_mismatch_and_missing_time_context()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState state = RichCheat(round);
            TableProjectionContext context = V32Fixtures.Context(round);
            PlayerSnapshotView seat0 = TableVisibilityProjection.ProjectPlayerSnapshot(round, state, 0, context);
            TableOwnerPrivateView owner0 = TableVisibilityProjection.ProjectOwnerPrivate(round, state, 0, context);
            TableOwnerPrivateView owner1 = TableVisibilityProjection.ProjectOwnerPrivate(round, state, 1, context);

            Assert.Throws<TableProjectionException>(() =>
                TableProjectionContractMapper.MapPlayerSnapshot(seat0, owner1, new TableProjectionTimeContext(1200)));
            Assert.Throws<ArgumentNullException>(() =>
                TableProjectionContractMapper.MapPlayerSnapshot(seat0, owner0, null!));
        }

        private static CheatState RichCheat(RoundStateMachine round)
        {
            CheatState state = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(), round.RoundId);
            state = V32Fixtures.WithLedger(
                state,
                new CheatLedgerEntry("ledger-0", 0, -5, 995, "TILE_MARK"));

            TileMarkTransition mark = state.TileMarks.AddGroup(new TileMarkGroup(
                "group-0",
                0,
                new[] { new TileId(0), new TileId(1), new TileId(2), new TileId(3) },
                round.RoundId,
                1));
            Assert.True(mark.Success);
            state = state.WithTileMarks(mark.State);

            SleeveBloomState sleeve = state.SleeveBloom;
            sleeve = sleeve.WithImmediateOffer(new SleeveBloomOffer(
                "immediate-0",
                0,
                SleeveBloomDrawKind.Normal,
                new TileId(4),
                new TileId(5),
                1,
                SleeveBloomOfferMode.Immediate));
            sleeve = sleeve.WithOpenedWindow(0, 3, out string windowId);
            var offTurn = new SleeveBloomOffer(
                "offturn-0",
                0,
                SleeveBloomDrawKind.Normal,
                new TileId(6),
                new TileId(7),
                3,
                SleeveBloomOfferMode.OffTurn,
                windowId,
                2000L,
                1000L);
            sleeve = sleeve.WithOffTurnOffer(offTurn);
            sleeve = sleeve.WithReservation(new SleeveBloomReservation(
                0,
                offTurn.OfferId,
                new TileId(6),
                windowId,
                1000L));
            state = state.WithSleeveBloom(sleeve);

            var beam = new BeamSwapOffer(
                "beam-0",
                0,
                BeamSwapWindow.Wall,
                new[] { new BeamSwapCandidate(new TileId(8), BeamSwapSlot.Wall(54)) },
                new[] { new TileId(9) },
                new TileId(10),
                1);
            return state.WithBeamSwap(state.BeamSwap.WithOffer(beam));
        }

        private static RoundStateMachine WithSleeve(RoundStateMachine source, int seat)
        {
            IReadOnlyList<TileId> hand = source.Tiles.TilesInZone(TileZone.Hand, seat);
            if (hand.Count < 2)
            {
                throw new InvalidOperationException("Fixture needs at least two hand tiles for a sleeve.");
            }

            List<TilePlacement> placements = source.Tiles.AsPlacements().ToList();
            for (int i = 0; i < 2; i++)
            {
                int index = placements.FindIndex(item => item.Tile == hand[i]);
                if (index < 0)
                {
                    throw new InvalidOperationException("Fixture sleeve tile disappeared.");
                }

                placements[index] = new TilePlacement(hand[i], TilePosition.Sleeve(seat, i));
            }

            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                source.Tiles.WallPointer,
                source.Tiles.WallEndExclusive,
                source.Tiles.RinshanPointer,
                source.Tiles.LastDiscard,
                source.Tiles.Revision,
                source.Tiles.LastRinshanDraw,
                source.Tiles.DoraIndicators,
                source.Tiles.KanCount,
                source.Tiles.DrawnTile);
            return RoundStateMachine.CreateDealt(source.Spec, map, source.Phase);
        }
    }
}
