using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class PonChiRiichiEventProjectionTests
    {
        [Theory]
        [InlineData(RoundEventType.PonDeclared, ProtocolEventType.PonDeclared)]
        [InlineData(RoundEventType.ChiDeclared, ProtocolEventType.ChiDeclared)]
        [InlineData(RoundEventType.RiichiDeclared, ProtocolEventType.RiichiDeclared)]
        public void from_round_event_preserves_the_committed_identity(RoundEventType sourceType, ProtocolEventType protocolType)
        {
            var record = new TableEventRecord(
                new EventSeq(43), new StateVersion(17), 2, sourceType, 2,
                sourceType == RoundEventType.RiichiDeclared ? new TileId(35) : new TileId(72),
                sourceType == RoundEventType.PonDeclared ? "PON/0" : sourceType == RoundEventType.ChiDeclared ? "CHI/0" : "RIICHI");

            TableEventProjectionInput input = TableEventProjection.FromRoundEvent(record)!;

            Assert.NotNull(input);
            Assert.Equal(protocolType, input.Type);
            Assert.Equal(record.Seq, input.EventSeq);
            Assert.Equal(record.CommandVersion, input.CommandVersion);
            Assert.Equal(2u, input.EventOrdinal);
            Assert.Equal(2, input.Seat);
            Assert.Equal(sourceType == RoundEventType.RiichiDeclared ? 0 : 1, input.TileIds.Count);
            if (sourceType != RoundEventType.RiichiDeclared) Assert.Equal(72u, input.TileIds[0]);
        }

        [Theory]
        [InlineData(ProtocolEventType.PonDeclared, ProtocolMeldType.Pon)]
        [InlineData(ProtocolEventType.ChiDeclared, ProtocolMeldType.Chi)]
        public void called_tile_source_maps_to_full_public_payload_for_all_audiences(ProtocolEventType type, ProtocolMeldType meldType)
        {
            uint[] meldTiles = type == ProtocolEventType.PonDeclared
                ? new uint[] { 0, 1, 2 }
                : new uint[] { 0, 4, 8 };
            uint calledTile = type == ProtocolEventType.PonDeclared ? 2u : 4u;
            var input = new TableEventProjectionInput(type, new EventSeq(19), new StateVersion(8), 3, 2, new[] { calledTile });
            var meld = new MeldView(meldType, meldTiles, calledTile, 1);
            var facts = new TableProjectionEventFacts(turnNumber: 6, meld: meld, fromSeat: 1);

            for (int seat = 0; seat < 4; seat++)
            {
                TableProjectedEvent projected = TableEventProjection.Project(input, TableProjectionAudience.ForSeat(seat), new TableProjectionPolicy())!;
                ProtocolEventRecord mapped = TableProjectionContractMapper.MapEvent(projected, facts);
                Assert.Equal(type, mapped.Type);
                Assert.Equal(new EventSeq(19), mapped.EventSeq);
                Assert.Equal(new StateVersion(8), mapped.CommandVersion);
                Assert.Equal(3u, mapped.EventOrdinal);
                Assert.Equal(ProtocolEventVisibility.Public, mapped.Visibility);
                Assert.Equal(2, mapped.ProjectionPayload!.Seat);
                Assert.Equal(6u, mapped.ProjectionPayload.TurnNumber);
                Assert.Equal(meldType, mapped.ProjectionPayload.Meld!.Type);
                Assert.Equal(meldTiles, mapped.ProjectionPayload.Meld.TileIds.ToArray());
                Assert.Equal(calledTile, mapped.ProjectionPayload.Meld.CalledTileId);
                Assert.Equal(1, mapped.ProjectionPayload.FromSeat);
            }

            ProtocolEventRecord spectator = TableProjectionContractMapper.MapEvent(
                TableEventProjection.Project(input, TableProjectionAudience.Spectator, new TableProjectionPolicy())!, facts);
            Assert.Equal(ProtocolEventVisibility.Public, spectator.Visibility);
            Assert.Equal(meldTiles, spectator.ProjectionPayload!.Meld!.TileIds.ToArray());
        }

        [Fact]
        public void malformed_call_facts_fail_closed_at_the_final_payload_boundary()
        {
            var input = new TableEventProjectionInput(
                ProtocolEventType.PonDeclared, new EventSeq(9), new StateVersion(5), 1, 3, new[] { 34u });
            TableProjectedEvent projected = TableEventProjection.Project(input, TableProjectionAudience.Spectator, new TableProjectionPolicy())!;

            Assert.Throws<TableProjectionException>(() => TableProjectionContractMapper.MapEvent(projected, null));
            Assert.Throws<ArgumentException>(() => new MeldView(ProtocolMeldType.Pon, new uint[] { 32, 34 }, 34, 0));
            Assert.Throws<ArgumentException>(() => new MeldView(ProtocolMeldType.Pon, new uint[] { 32, 34, 34 }, 34, 0));
            Assert.Throws<TableProjectionException>(() => TableProjectionContractMapper.MapEvent(
                projected, new TableProjectionEventFacts(1, meld: new MeldView(ProtocolMeldType.Chi, new uint[] { 32, 34, 35 }, 34, 0), fromSeat: 0)));
            Assert.Throws<TableProjectionException>(() => TableProjectionContractMapper.MapEvent(
                projected, new TableProjectionEventFacts(1, meld: new MeldView(ProtocolMeldType.Pon, new uint[] { 32, 33, 34 }, 32, 0), fromSeat: 0)));
            Assert.Throws<TableProjectionException>(() => TableProjectionContractMapper.MapEvent(
                projected, new TableProjectionEventFacts(1, meld: new MeldView(ProtocolMeldType.Pon, new uint[] { 32, 33, 34 }, 34, 1), fromSeat: 0)));
        }

        [Fact]
        public void committed_domain_pon_facts_bind_the_physical_call_not_a_later_meld()
        {
            TileId called = Physical(30, 3);
            TileId handA = Physical(30, 0);
            TileId handB = Physical(30, 1);
            TileId addedKanTile = Physical(30, 2);
            RoundStateMachine initial = BuildRound("projection-real-pon", new[] { Physical(0, 0) }, called,
                new[] { handA, handB, addedKanTile });
            const int caller = 1;
            RoundTransition discard = initial.Execute(RoundCommand.Discard(initial.RoundId, 0, called, new[] { caller }));
            Assert.True(discard.IsAccepted, discard.Error?.ToString());
            RoundTransition pon = discard.State!.Execute(RoundCommand.Pon(initial.RoundId, caller, called, handA, handB));
            Assert.True(pon.IsAccepted, pon.Error?.ToString());
            RoundStateMachine committed = pon.State!;
            RoundEvent domainEvent = Assert.Single(committed.Events, value => value.Type == RoundEventType.PonDeclared);
            TableEventRecord record = ToRecord(committed, domainEvent, new EventSeq(5), new StateVersion(2), 0);

            Assert.Throws<InvalidOperationException>(() => TableCommittedCallFacts.RequireCallMeld(initial, record));
            RoundStateMachine wrongRound = BuildRound("projection-other-round",
                new[] { Physical(0, 0) }, called, new[] { handA, handB, addedKanTile });
            Assert.Throws<InvalidOperationException>(() => TableCommittedCallFacts.RequireCallMeld(wrongRound, record));
            Assert.Throws<InvalidOperationException>(() => TableCommittedCallFacts.RequireCallMeld(committed,
                new TableEventRecord(record.Seq, record.CommandVersion, record.Ordinal, record.Type, 2, record.Tile, record.Detail)));
            Assert.Throws<InvalidOperationException>(() => TableCommittedCallFacts.RequireCallMeld(committed,
                new TableEventRecord(record.Seq, record.CommandVersion, record.Ordinal, record.Type, record.Seat,
                    Physical(30, 2), record.Detail)));
            Assert.Throws<InvalidOperationException>(() => TableCommittedCallFacts.RequireCallMeld(committed,
                new TableEventRecord(record.Seq, record.CommandVersion, record.Ordinal, record.Type, record.Seat,
                    record.Tile, "PON/3")));

            // Later state upgrades this exact Pon; the old committed event must still project
            // the original three physical entities, excluding the added-kan tile.
            RoundTransition addedKan = committed.Execute(RoundCommand.AddedKan(committed.RoundId, caller, addedKanTile));
            Assert.True(addedKan.IsAccepted, addedKan.Error?.ToString());
            committed = addedKan.State!;

            MeldView facts = TableCommittedCallFacts.RequireCallMeld(committed, record);
            TableEventProjectionInput input = TableEventProjection.FromRoundEvent(record)!;
            TableProjectedEvent projected = TableEventProjection.Project(input, TableProjectionAudience.Spectator, new TableProjectionPolicy())!;
            ProtocolEventRecord payload = TableProjectionContractMapper.MapEvent(projected,
                new TableProjectionEventFacts(
                    turnNumber: TableCommittedCallFacts.RequireTurnNumber(committed, record),
                    meld: facts,
                    fromSeat: facts.FromSeat));

            Assert.Equal(new[] { unchecked((uint)handA.Value), unchecked((uint)handB.Value), unchecked((uint)called.Value) }, facts.TileIds.ToArray());
            Assert.Equal(caller, payload.ProjectionPayload!.Seat);
            Assert.Equal(unchecked((uint)domainEvent.Tile!.Value.Value), payload.ProjectionPayload.Meld!.CalledTileId);
            Assert.Equal(domainEvent.Seat, payload.ProjectionPayload.Seat);
            Assert.Equal(record.Seq, payload.EventSeq);
            Assert.Equal(record.CommandVersion, payload.CommandVersion);
            Assert.Equal((uint)record.Ordinal, payload.EventOrdinal);
            Assert.Equal(136, TilePositionMap.Validate(committed.Tiles).AssignedCount);
        }

        [Fact]
        public void committed_domain_chi_and_riichi_use_their_own_source_facts()
        {
            TileId chiCalled = Physical(2, 3);
            TileId chiA = Physical(0, 0);
            TileId chiB = Physical(1, 0);
            RoundStateMachine chiInitial = BuildRound("projection-real-chi", new[] { Physical(0, 1) }, chiCalled, new[] { chiA, chiB });
            RoundTransition chiDiscard = chiInitial.Execute(RoundCommand.Discard(chiInitial.RoundId, 0, chiCalled, new[] { 1 }));
            Assert.True(chiDiscard.IsAccepted, chiDiscard.Error?.ToString());
            RoundTransition chi = chiDiscard.State!.Execute(RoundCommand.Chi(chiInitial.RoundId, 1, chiCalled, chiA, chiB));
            Assert.True(chi.IsAccepted, chi.Error?.ToString());
            RoundStateMachine chiRound = chi.State!;
            RoundEvent chiEvent = Assert.Single(chiRound.Events, value => value.Type == RoundEventType.ChiDeclared);
            TableEventRecord chiRecord = ToRecord(chiRound, chiEvent, new EventSeq(11), new StateVersion(2), 1);
            MeldView chiFacts = TableCommittedCallFacts.RequireCallMeld(chiRound, chiRecord);
            Assert.Equal(new[] { (uint)chiA.Value, (uint)chiB.Value, (uint)chiCalled.Value }, chiFacts.TileIds.ToArray());
            Assert.Equal(0, chiFacts.FromSeat);

            TileId[] riichiHand =
            {
                Physical(0, 0), Physical(1, 0), Physical(2, 0), Physical(3, 0), Physical(4, 0),
                Physical(5, 0), Physical(6, 0), Physical(7, 0), Physical(8, 0),
                Physical(9, 0), Physical(9, 1), Physical(22, 0), Physical(23, 0)
            };
            TileId riichiDiscardTile = Physical(11, 0);
            RoundStateMachine riichiInitial = BuildRound("projection-real-riichi", riichiHand, riichiDiscardTile, null);
            RoundTransition riichi = riichiInitial.Execute(RoundCommand.Riichi(riichiInitial.RoundId, 0, riichiDiscardTile, new[] { 1, 2, 3 }));
            Assert.True(riichi.IsAccepted, riichi.Error?.ToString());
            RoundStateMachine riichiRound = riichi.State!;
            RoundEvent riichiEvent = Assert.Single(riichiRound.Events, value => value.Type == RoundEventType.RiichiDeclared);
            TableEventRecord riichiRecord = ToRecord(riichiRound, riichiEvent, new EventSeq(20), new StateVersion(1), 0);
            TableEventProjectionInput riichiInput = TableEventProjection.FromRoundEvent(riichiRecord)!;
            TableProjectedEvent riichiProjected = TableEventProjection.Project(riichiInput, TableProjectionAudience.Spectator, new TableProjectionPolicy())!;
            ProtocolEventRecord riichiPayload = TableProjectionContractMapper.MapEvent(riichiProjected,
                new TableProjectionEventFacts(
                    turnNumber: TableCommittedCallFacts.RequireTurnNumber(riichiRound, riichiRecord),
                    discardTileId: (uint)riichiDiscardTile.Value,
                    doubleRiichi: TableCommittedCallFacts.RequireDoubleRiichi(riichiRound, riichiRecord)));
            Assert.Equal(0, riichiPayload.ProjectionPayload!.Seat);
            Assert.Equal((uint)riichiDiscardTile.Value, riichiPayload.ProjectionPayload.DiscardTileId);
            Assert.True(riichiPayload.ProjectionPayload.DoubleRiichi);
        }

        internal static TileId Physical(int typeIndex, int copy) => TileId.FromTypeIndex(typeIndex, copy);

        private static TableEventRecord ToRecord(RoundStateMachine round, RoundEvent value, EventSeq seq, StateVersion version, int ordinal) =>
            new TableEventRecord(seq, version, ordinal, value.Type, value.Seat, value.Tile, value.Detail);

        internal static RoundStateMachine BuildRound(string roundId, IReadOnlyList<TileId> dealerHand, TileId dealerDrawn, IReadOnlyList<TileId>? seat1Hand)
        {
            var placements = new TilePlacement[TileId.Count];
            var used = new bool[TileId.Count];
            void Place(TileId tile, TilePosition position)
            {
                if (used[tile.Value]) throw new InvalidOperationException("duplicate test tile " + tile.Value);
                used[tile.Value] = true;
                placements[tile.Value] = new TilePlacement(tile, position);
            }

            for (int i = 0; i < dealerHand.Count; i++) Place(dealerHand[i], TilePosition.Hand(0, i));
            if (dealerHand.Count > 13) throw new ArgumentException("Dealer hand contains more than 13 concealed tiles.");
            if (seat1Hand != null)
            {
                if (seat1Hand.Count > 13) throw new ArgumentException("Seat 1 hand contains more than 13 tiles.");
                for (int i = 0; i < seat1Hand.Count; i++) Place(seat1Hand[i], TilePosition.Hand(1, i));
            }

            Place(dealerDrawn, TilePosition.Hand(0, 13));
            for (int seat = 0; seat < 4; seat++)
            {
                int existing = seat == 0 ? dealerHand.Count : seat == 1 ? (seat1Hand?.Count ?? 0) : 0;
                for (int slot = existing; slot < 13; slot++)
                {
                    int tile = Array.FindIndex(used, value => !value);
                    if (tile < 0) throw new InvalidOperationException("fixture ran out of hand tiles");
                    Place(new TileId(tile), TilePosition.Hand(seat, slot));
                }
            }
            int doraTile = -1;
            for (int slot = 0; slot < 14; slot++)
            {
                int tile = Array.FindIndex(used, value => !value);
                if (tile < 0) throw new InvalidOperationException("fixture ran out of dead-wall tiles");
                Place(new TileId(tile), TilePosition.DeadWall(slot));
                if (slot == 4) doraTile = tile;
            }

            int wallCount = 0;
            for (int tile = 0; tile < TileId.Count; tile++)
            {
                if (used[tile]) continue;
                Place(new TileId(tile), TilePosition.Wall(wallCount++));
            }

            TilePositionMap map = TilePositionMap.FromPlacements(
                placements, 0, wallCount, 0,
                doraIndicators: new[] { new TileId(doraTile) },
                drawnTile: new DrawnTileEvidence(dealerDrawn, 0, DrawSource.Wall));
            return RoundStateMachine.CreateDealt(
                new RoundSpec(roundId, 0, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0),
                map,
                RoundPhase.CheatWindow);
        }
    }
}
