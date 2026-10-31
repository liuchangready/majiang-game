using System;
using System.Collections.Generic;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Mahjong.TableService;
using Xunit;

namespace Mahjong.TableService.Tests
{
    public sealed class FirstTurnDoubleRiichiProjectionTests
    {
        [Fact]
        public void table_service_facts_keep_non_dealer_first_turn_double_riichi()
        {
            RoundStateMachine initial = BuildFirstTurnRiichiRound("service-first-turn-riichi");
            RoundTransition dealerDiscard = initial.Execute(RoundCommand.Discard(initial.RoundId, 0, Tile(22, 1)));
            Assert.True(dealerDiscard.IsAccepted, dealerDiscard.Error?.ToString());
            RoundTransition riichi = dealerDiscard.State!.Execute(RoundCommand.Riichi(
                initial.RoundId, 1, Tile(0, 3), new[] { 2 }));
            Assert.True(riichi.IsAccepted, riichi.Error?.ToString());

            var records = new List<TableEventRecord>();
            Append(records, dealerDiscard.Events, new StateVersion(1));
            Append(records, riichi.Events, new StateVersion(2));
            var request = new TableProjectionContextRequest(
                "service-first-turn-riichi", riichi.State!, new StateVersion(2), new EventSeq((ulong)records.Count),
                new OwnerEpoch(1), 0, 0, 100, 1_760_000_000_000, records);
            var source = new TableProjectionContextSource(initialRound: initial);

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
    }
}
