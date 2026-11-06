using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.LocalHost;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalCallEventFactsTests
    {
        [Fact]
        public void local_snapshot_facts_rebuild_a_real_committed_pon_from_its_domain_history()
        {
            TileId called = Tile(30, 3);
            TileId handA = Tile(30, 0);
            TileId handB = Tile(30, 1);
            RoundStateMachine initial = BuildRound("local-projection-pon", new[] { Tile(0, 0) }, called, new[] { handA, handB });
            RoundTransition discard = initial.Execute(RoundCommand.Discard(initial.RoundId, 0, called, new[] { 1 }));
            Assert.True(discard.IsAccepted, discard.Error?.ToString());
            RoundTransition pon = discard.State!.Execute(RoundCommand.Pon(initial.RoundId, 1, called, handA, handB));
            Assert.True(pon.IsAccepted, pon.Error?.ToString());

            var records = new List<TableEventRecord>();
            Append(records, discard.Events, new StateVersion(1));
            Append(records, pon.Events, new StateVersion(2));
            var request = new TableProjectionContextRequest(
                "local-room", pon.State!, new StateVersion(2), new EventSeq((ulong)records.Count),
                new OwnerEpoch(1), 0, 0, 0, 1, records);
            var clock = SystemTableClock.Instance;
            var source = new LocalSnapshotFacts(LocalHostEnvironment.System, clock);

            TableProjectionContextData facts = source.Capture(request);
            TableProjectionEventFact call = Assert.Single(facts.EventFacts, value => value.Type == ProtocolEventType.PonDeclared);
            Assert.Equal(new EventSeq(4), call.EventSeq);
            Assert.Equal(new StateVersion(2), call.CommandVersion);
            Assert.Equal(1u, call.EventOrdinal);
            Assert.Equal(1u, call.Facts.TurnNumber);
            Assert.Equal(0, call.Facts.FromSeat);
            Assert.Equal(ProtocolMeldType.Pon, call.Facts.Meld!.Type);
            Assert.Equal(new[] { (uint)handA.Value, (uint)handB.Value, (uint)called.Value }, call.Facts.Meld.TileIds.ToArray());
            Assert.Equal((uint)called.Value, call.Facts.Meld.CalledTileId);
            Assert.Equal(136, TilePositionMap.Validate(pon.State!.Tiles).AssignedCount);

            source.RecoveredFacts = new TableProjectionContextData(
                facts.RoundId, facts.StateVersion, facts.SessionVersion, facts.Cheat,
                facts.PublicFacts, facts.PrivateFacts, facts.Session, facts.Policy,
                facts.EventFacts.Where(value => value.Type != ProtocolEventType.PonDeclared).ToArray(),
                facts.ConcealedMeldFacts);
            TableProjectionContextData recovered = source.Capture(request);
            Assert.Contains(recovered.EventFacts, value => value.Type == ProtocolEventType.TileDiscarded);
            TableProjectionEventFact recoveredCall = Assert.Single(recovered.EventFacts,
                value => value.Type == ProtocolEventType.PonDeclared);
            Assert.Equal(call.EventSeq, recoveredCall.EventSeq);
            Assert.Equal(call.CommandVersion, recoveredCall.CommandVersion);
            Assert.Equal(call.EventOrdinal, recoveredCall.EventOrdinal);
            Assert.Equal(call.Facts.Meld!.TileIds, recoveredCall.Facts.Meld!.TileIds);
        }

        [Fact]
        public void local_snapshot_facts_preserve_non_dealer_first_turn_double_riichi()
        {
            RoundStateMachine initial = BuildFirstTurnRiichiRound("local-first-turn-riichi");
            RoundTransition dealerDiscard = initial.Execute(RoundCommand.Discard(initial.RoundId, 0, Tile(22, 1)));
            Assert.True(dealerDiscard.IsAccepted, dealerDiscard.Error?.ToString());
            RoundTransition riichi = dealerDiscard.State!.Execute(RoundCommand.Riichi(
                initial.RoundId, 1, Tile(0, 3), new[] { 2 }));
            Assert.True(riichi.IsAccepted, riichi.Error?.ToString());

            var records = new List<TableEventRecord>();
            Append(records, dealerDiscard.Events, new StateVersion(1));
            Append(records, riichi.Events, new StateVersion(2));
            var request = new TableProjectionContextRequest(
                "local-first-turn-riichi", riichi.State!, new StateVersion(2), new EventSeq((ulong)records.Count),
                new OwnerEpoch(1), 0, 0, 0, 1, records);
            var source = new LocalSnapshotFacts(LocalHostEnvironment.System, SystemTableClock.Instance);

            TableProjectionContextData facts = source.Capture(request);
            TableProjectionEventFact fact = Assert.Single(facts.EventFacts,
                value => value.Type == ProtocolEventType.RiichiDeclared);
            Assert.Equal(2u, fact.Facts.TurnNumber);
            Assert.Equal((uint)Tile(0, 3).Value, fact.Facts.DiscardTileId);
            Assert.True(fact.Facts.DoubleRiichi);
        }

        private static void Append(List<TableEventRecord> target, IReadOnlyList<RoundEvent> events, StateVersion version)
        {
            for (int i = 0; i < events.Count; i++)
            {
                RoundEvent value = events[i];
                target.Add(new TableEventRecord(new EventSeq((ulong)target.Count + 1), version, i,
                    value.Type, value.Seat, value.Tile, value.Detail));
            }
        }

        private static TileId Tile(int type, int copy) => TileId.FromTypeIndex(type, copy);

        private static RoundStateMachine BuildFirstTurnRiichiRound(string roundId)
        {
            var placements = new TilePlacement[TileId.Count];
            var used = new bool[TileId.Count];
            void Place(TileId tile, TilePosition position)
            {
                if (used[tile.Value]) throw new InvalidOperationException("Duplicate fixture tile " + tile.Value);
                used[tile.Value] = true;
                placements[tile.Value] = new TilePlacement(tile, position);
            }

            TileId[] dealerHand = { Tile(0, 0), Tile(0, 1), Tile(0, 2), Tile(8, 0), Tile(8, 1), Tile(8, 2),
                Tile(9, 0), Tile(9, 1), Tile(9, 2), Tile(18, 0), Tile(18, 1), Tile(18, 2), Tile(22, 0) };
            TileId[] riichiHand = { Tile(1, 0), Tile(2, 0), Tile(3, 0), Tile(1, 1), Tile(2, 1), Tile(3, 1),
                Tile(5, 0), Tile(6, 0), Tile(7, 0), Tile(19, 0), Tile(20, 0), Tile(21, 0), Tile(22, 2) };
            for (int i = 0; i < dealerHand.Length; i++) Place(dealerHand[i], TilePosition.Hand(0, i));
            Place(Tile(22, 1), TilePosition.Hand(0, 13));
            for (int i = 0; i < riichiHand.Length; i++) Place(riichiHand[i], TilePosition.Hand(1, i));
            Place(Tile(0, 3), TilePosition.Wall(0));
            for (int seat = 2; seat < 4; seat++)
                for (int slot = 0; slot < 13; slot++)
                    Place(new TileId(Array.FindIndex(used, value => !value)), TilePosition.Hand(seat, slot));
            int dora = -1;
            for (int slot = 0; slot < 14; slot++)
            {
                int tile = Array.FindIndex(used, value => !value);
                Place(new TileId(tile), TilePosition.DeadWall(slot));
                if (slot == 4) dora = tile;
            }
            int wallCount = 1;
            for (int tile = 0; tile < TileId.Count; tile++)
                if (!used[tile]) Place(new TileId(tile), TilePosition.Wall(wallCount++));

            TilePositionMap map = TilePositionMap.FromPlacements(placements, 0, wallCount, 0,
                doraIndicators: new[] { new TileId(dora) },
                drawnTile: new DrawnTileEvidence(Tile(22, 1), 0, DrawSource.Wall));
            return RoundStateMachine.CreateDealt(
                new RoundSpec(roundId, 0, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0),
                map, RoundPhase.CheatWindow);
        }

        private static RoundStateMachine BuildRound(string roundId, IReadOnlyList<TileId> dealerHand, TileId drawn, IReadOnlyList<TileId> callerHand)
        {
            var placements = new TilePlacement[TileId.Count];
            var used = new bool[TileId.Count];
            void Place(TileId tile, TilePosition position)
            {
                if (used[tile.Value]) throw new InvalidOperationException("Duplicate fixture tile " + tile.Value);
                used[tile.Value] = true;
                placements[tile.Value] = new TilePlacement(tile, position);
            }

            for (int i = 0; i < dealerHand.Count; i++) Place(dealerHand[i], TilePosition.Hand(0, i));
            for (int i = 0; i < callerHand.Count; i++) Place(callerHand[i], TilePosition.Hand(1, i));
            Place(drawn, TilePosition.Hand(0, 13));
            for (int seat = 0; seat < 4; seat++)
            {
                int existing = seat == 0 ? dealerHand.Count : seat == 1 ? callerHand.Count : 0;
                for (int slot = existing; slot < 13; slot++)
                {
                    int tile = Array.FindIndex(used, value => !value);
                    Place(new TileId(tile), TilePosition.Hand(seat, slot));
                }
            }

            int dora = -1;
            for (int slot = 0; slot < 14; slot++)
            {
                int tile = Array.FindIndex(used, value => !value);
                Place(new TileId(tile), TilePosition.DeadWall(slot));
                if (slot == 4) dora = tile;
            }

            int wallCount = 0;
            for (int tile = 0; tile < TileId.Count; tile++)
                if (!used[tile]) Place(new TileId(tile), TilePosition.Wall(wallCount++));

            TilePositionMap map = TilePositionMap.FromPlacements(placements, 0, wallCount, 0,
                doraIndicators: new[] { new TileId(dora) },
                drawnTile: new DrawnTileEvidence(drawn, 0, DrawSource.Wall));
            return RoundStateMachine.CreateDealt(
                new RoundSpec(roundId, 0, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0),
                map,
                RoundPhase.CheatWindow);
        }
    }
}
