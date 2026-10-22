using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Domain.Tests
{
    public sealed class TileStateHashTests
    {
        [Fact]
        public void SameMapAlwaysProducesTheSameStableHash()
        {
            TilePositionMap map = TestTables.Opening();
            ulong first = TileStateHash.Compute(map);
            ulong second = TileStateHash.Compute(map);

            Assert.Equal(first, second);
            Assert.Equal(first, map.StateHash);
            Assert.Equal(16, TileStateHash.Format(first).Length);
            Assert.Equal(TileStateHash.Format(first), TileStateHash.Format(second));
            Assert.Equal(3, TileStateHash.EncodingVersion);
            Assert.Throws<ArgumentNullException>(() => TileStateHash.Compute(null!));
        }

        [Fact]
        public void PlacementConstructionOrderDoesNotChangeTheHash()
        {
            TilePositionMap map = TestTables.Opening();
            IReadOnlyList<TilePlacement> placements = map.AsPlacements();
            TilePlacement[] reversed = placements.Reverse().ToArray();

            TilePositionMap forward = TilePositionMap.FromPlacements(
                placements,
                map.WallPointer,
                map.WallEndExclusive,
                map.RinshanPointer,
                null,
                0,
                null,
                map.DoraIndicators,
                drawnTile: map.DrawnTile);
            TilePositionMap backward = TilePositionMap.FromPlacements(
                reversed,
                map.WallPointer,
                map.WallEndExclusive,
                map.RinshanPointer,
                null,
                0,
                null,
                map.DoraIndicators,
                drawnTile: map.DrawnTile);

            Assert.Equal(forward.StateHash, backward.StateHash);
            Assert.Equal(map.StateHash, forward.StateHash);
        }

        [Fact]
        public void RevisionIsHistoryAndDoesNotChangeTheHash()
        {
            RecordedRandomDraw recorded = RecordedRandomDraw.Standard("revision");
            TilePositionMap direct = TilePositionMap.CreateDealt(recorded, 0);
            TilePositionMap rehydrated = TilePositionMap.FromPlacements(
                direct.AsPlacements(),
                direct.WallPointer,
                direct.WallEndExclusive,
                direct.RinshanPointer,
                null,
                7,
                null,
                direct.DoraIndicators,
                drawnTile: direct.DrawnTile);

            Assert.Equal(0, direct.Revision);
            Assert.Equal(7, rehydrated.Revision);
            Assert.Equal(direct.StateHash, rehydrated.StateHash);
        }

        [Fact]
        public void ChangingAnAssignedPositionChangesTheHash()
        {
            TilePositionMap map = TestTables.Opening();
            List<TilePlacement> placements = map.AsPlacements().ToList();
            TileId tile = TestTables.HandTile(map, 1, 0);
            int index = placements.FindIndex(placement => placement.Tile == tile);
            placements[index] = new TilePlacement(tile, TilePosition.Discard(1, 0));

            TilePositionMap moved = TilePositionMap.FromPlacements(
                placements,
                map.WallPointer,
                map.WallEndExclusive,
                map.RinshanPointer,
                null,
                0,
                null,
                map.DoraIndicators,
                drawnTile: map.DrawnTile);

            Assert.NotEqual(map.StateHash, moved.StateHash);
            Assert.Equal(map.StateHash, TileStateHash.Compute(map));
        }

        [Fact]
        public void ChangingOnlyACursorChangesTheHash()
        {
            TilePositionMap map = TestTables.Opening();
            TilePositionMap pointerForward = TilePositionMap.FromPlacements(
                map.AsPlacements(),
                map.WallPointer + 1,
                map.WallEndExclusive,
                map.RinshanPointer,
                null,
                0,
                null,
                map.DoraIndicators,
                drawnTile: map.DrawnTile);
            TilePositionMap pointerBack = TilePositionMap.FromPlacements(
                map.AsPlacements(),
                map.WallPointer - 1,
                map.WallEndExclusive,
                map.RinshanPointer,
                null,
                0,
                null,
                map.DoraIndicators,
                drawnTile: map.DrawnTile);

            Assert.NotEqual(map.StateHash, pointerForward.StateHash);
            Assert.NotEqual(map.StateHash, pointerBack.StateHash);
            Assert.NotEqual(pointerForward.StateHash, pointerBack.StateHash);
        }

        [Fact]
        public void MovingBetweenSeatsOrZonesChangesTheHash()
        {
            TilePositionMap map = TestTables.Opening();
            TileId tile = TestTables.HandTile(map, 1, 0);
            List<TilePlacement> toSeatTwo = map.AsPlacements().ToList();
            int index = toSeatTwo.FindIndex(placement => placement.Tile == tile);
            toSeatTwo[index] = new TilePlacement(tile, TilePosition.Hand(2, 13));

            List<TilePlacement> toDiscard = map.AsPlacements().ToList();
            toDiscard[index] = new TilePlacement(tile, TilePosition.Discard(1, 0));

            TilePositionMap seatTwo = TilePositionMap.FromPlacements(
                toSeatTwo,
                map.WallPointer,
                map.WallEndExclusive,
                map.RinshanPointer,
                null,
                0,
                null,
                map.DoraIndicators,
                drawnTile: map.DrawnTile);
            TilePositionMap discard = TilePositionMap.FromPlacements(
                toDiscard,
                map.WallPointer,
                map.WallEndExclusive,
                map.RinshanPointer,
                null,
                0,
                null,
                map.DoraIndicators,
                drawnTile: map.DrawnTile);

            Assert.NotEqual(map.StateHash, seatTwo.StateHash);
            Assert.NotEqual(map.StateHash, discard.StateHash);
            Assert.NotEqual(seatTwo.StateHash, discard.StateHash);
        }

        [Fact]
        public void LastDiscardEvidenceIsPartOfTheHash()
        {
            TilePositionMap map = TestTables.Opening();
            TileId tile = TestTables.HandTile(map, 0, 0);
            TilePositionMap discarded = TestTables.Require(map.Discard(0, tile));
            LastDiscardEvidence evidence = discarded.LastDiscard!.Value;

            TilePositionMap rehydratedWithoutEvidence = TilePositionMap.FromPlacements(
                discarded.AsPlacements(),
                discarded.WallPointer,
                discarded.WallEndExclusive,
                discarded.RinshanPointer,
                null,
                0,
                null,
                discarded.DoraIndicators);
            TilePositionMap rehydratedWithEvidence = TilePositionMap.FromPlacements(
                discarded.AsPlacements(),
                discarded.WallPointer,
                discarded.WallEndExclusive,
                discarded.RinshanPointer,
                evidence,
                0,
                null,
                discarded.DoraIndicators);

            Assert.Equal(discarded.StateHash, rehydratedWithEvidence.StateHash);
            Assert.NotEqual(discarded.StateHash, rehydratedWithoutEvidence.StateHash);
        }

        [Fact]
        public void DoraIndicatorAndRinshanDrawEvidenceArePartOfTheHash()
        {
            TilePositionMap map = TestTables.Opening();
            TilePositionMap withoutDora = TilePositionMap.FromPlacements(
                map.AsPlacements(),
                map.WallPointer,
                map.WallEndExclusive,
                map.RinshanPointer,
                null,
                0,
                null,
                null,
                0,
                map.DrawnTile);
            Assert.NotEqual(map.StateHash, withoutDora.StateHash);

            TilePositionMap fourKans = TilePositionMap.FromPlacements(
                map.AsPlacements(),
                map.WallPointer,
                map.WallEndExclusive,
                map.RinshanPointer,
                null,
                0,
                null,
                map.DoraIndicators,
                4,
                map.DrawnTile);
            Assert.NotEqual(map.StateHash, fourKans.StateHash);

            TilePositionMap afterRinshan = TestTables.Require(map.DrawReplacement(1));
            RinshanDrawEvidence evidence = afterRinshan.LastRinshanDraw!.Value;
            TilePositionMap rehydratedWithEvidence = TilePositionMap.FromPlacements(
                afterRinshan.AsPlacements(),
                afterRinshan.WallPointer,
                afterRinshan.WallEndExclusive,
                afterRinshan.RinshanPointer,
                null,
                0,
                evidence,
                afterRinshan.DoraIndicators,
                0,
                afterRinshan.DrawnTile);
            TilePositionMap rehydratedWithoutEvidence = TilePositionMap.FromPlacements(
                afterRinshan.AsPlacements(),
                afterRinshan.WallPointer,
                afterRinshan.WallEndExclusive,
                afterRinshan.RinshanPointer,
                null,
                0,
                null,
                afterRinshan.DoraIndicators,
                0,
                afterRinshan.DrawnTile);

            Assert.Equal(afterRinshan.StateHash, rehydratedWithEvidence.StateHash);
            Assert.NotEqual(afterRinshan.StateHash, rehydratedWithoutEvidence.StateHash);
        }

        [Fact]
        public void RecordedRandomDrawRejectsDuplicatesMissingTilesAndNull()
        {
            TileId[] standard = TileId.All.ToArray();
            Assert.Equal(136, RecordedRandomDraw.Create("standard", standard).WallOrder.Count);

            TileId[] duplicate = (TileId[])standard.Clone();
            duplicate[1] = duplicate[0];
            Assert.Throws<ArgumentException>(() => RecordedRandomDraw.Create("dup", duplicate));

            TileId[] missing = standard.Take(135).ToArray();
            Assert.Throws<ArgumentException>(() => RecordedRandomDraw.Create("missing", missing));
            Assert.Throws<ArgumentNullException>(() => RecordedRandomDraw.Create("null", null!));
        }

        [Fact]
        public void RecordedDrawAppendIsImmutableAndKeepsCallerSuppliedId()
        {
            RecordedRandomDraw empty = RecordedRandomDraw.Standard("record-1");
            RecordedRandomDraw appended = empty.AppendDraw(1, new TileId(53), DrawSource.Wall);

            Assert.Empty(empty.Draws);
            Assert.Single(appended.Draws);
            Assert.Equal("record-1", appended.RecordId);
            Assert.Equal(1, appended.Draws[0].Seat);
            Assert.Equal(new TileId(53), appended.Draws[0].Tile);
            Assert.Equal(DrawSource.Wall, appended.Draws[0].Source);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                empty.AppendDraw(4, new TileId(0), DrawSource.Wall));
        }

        [Fact]
        public void RecordedWallOrderCannotBeMutatedThroughTheInterface()
        {
            RecordedRandomDraw record = RecordedRandomDraw.Standard("readonly");
            Assert.Throws<NotSupportedException>(() =>
                ((IList<TileId>)record.WallOrder)[0] = new TileId(1));
        }

        [Fact]
        public void RecordedDrawsReplayAgainstTheSameExplicitWallOrder()
        {
            RecordedRandomDraw record = RecordedRandomDraw.Standard("replay-1");
            TilePositionMap first = TilePositionMap.CreateDealt(record, 0);
            TilePositionMap second = TilePositionMap.CreateDealt(record, 0);

            for (int i = 0; i < 6; i++)
            {
                TransitionResult firstDraw = first.DrawFromWall(1);
                TransitionResult secondDraw = second.DrawFromWall(1);
                Assert.True(firstDraw.IsSuccess);
                Assert.True(secondDraw.IsSuccess);
                Assert.Equal(firstDraw.ResultTile, secondDraw.ResultTile);
                TileId tile = firstDraw.ResultTile.GetValueOrDefault();
                record = record.AppendDraw(1, tile, DrawSource.Wall);
                first = TestTables.Require(firstDraw.State!.Discard(1, tile));
                second = TestTables.Require(secondDraw.State!.Discard(1, tile));
                Assert.Equal(first.StateHash, second.StateHash);
            }

            TilePositionMap replay = TilePositionMap.CreateDealt(record, 0);
            for (int i = 0; i < record.Draws.Count; i++)
            {
                RecordedDraw entry = record.Draws[i];
                TransitionResult draw = replay.DrawFromWall(entry.Seat);
                Assert.True(draw.IsSuccess);
                Assert.Equal(entry.Tile, draw.ResultTile);
                replay = TestTables.Require(draw.State!.Discard(entry.Seat, entry.Tile));
            }

            Assert.Equal(first.StateHash, replay.StateHash);
        }
    }
}
