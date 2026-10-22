using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Domain.Tests
{
    internal static class TestTables
    {
        public static TilePositionMap Opening(int dealerSeat = 0)
        {
            return TilePositionMap.CreateDealt(RecordedRandomDraw.Standard("test-standard"), dealerSeat);
        }

        /// <summary>
        /// Builds an explicit wall order with selected absolute slots pinned; all other slots
        /// are filled with unused tile ids in ascending order. This is a test fixture, not a
        /// shuffle implementation.
        /// </summary>
        public static RecordedRandomDraw Crafted(params (int Slot, TileId Tile)[] assignments)
        {
            var tiles = new TileId[TileId.Count];
            var usedTiles = new bool[TileId.Count];
            var usedSlots = new bool[TileId.Count];

            for (int i = 0; i < assignments.Length; i++)
            {
                int slot = assignments[i].Slot;
                TileId tile = assignments[i].Tile;
                if (slot < 0 || slot >= TileId.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(assignments), slot, "Invalid wall slot.");
                }

                if (usedSlots[slot])
                {
                    throw new ArgumentException("Slot " + slot + " is assigned more than once.");
                }

                if (usedTiles[tile.Value])
                {
                    throw new ArgumentException("Tile " + tile + " is assigned more than once.");
                }

                usedSlots[slot] = true;
                usedTiles[tile.Value] = true;
                tiles[slot] = tile;
            }

            int nextTile = 0;
            for (int slot = 0; slot < tiles.Length; slot++)
            {
                if (usedSlots[slot])
                {
                    continue;
                }

                while (nextTile < TileId.Count && usedTiles[nextTile])
                {
                    nextTile++;
                }

                if (nextTile >= TileId.Count)
                {
                    throw new InvalidOperationException("Not enough unused tiles to fill the wall order.");
                }

                tiles[slot] = new TileId(nextTile);
                usedTiles[nextTile] = true;
            }

            return RecordedRandomDraw.Create("test-crafted", tiles);
        }

        public static TileId HandTile(TilePositionMap state, int seat, int ordinal = 0)
        {
            IReadOnlyList<TileId> hand = state.TilesInZone(TileZone.Hand, seat);
            if (ordinal < 0 || ordinal >= hand.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Hand ordinal is out of range.");
            }

            return hand[ordinal];
        }

        public static TileId TileAtWall(TilePositionMap state, int wallIndex)
        {
            return state.TileAt(TilePosition.Wall(wallIndex));
        }

        public static TileId TileAtDeadWall(TilePositionMap state, int deadWallIndex)
        {
            return state.TileAt(TilePosition.DeadWall(deadWallIndex));
        }

        public static TileId TileAtReplacement(TilePositionMap state, int replacementIndex)
        {
            return state.TileAt(TilePosition.DeadWallReplacement(replacementIndex));
        }

        public static TileId ClosedHandTile(TilePositionMap state, int seat)
        {
            TileId? drawn = state.DrawnTile?.Tile;
            IReadOnlyList<TileId> hand = state.TilesInZone(TileZone.Hand, seat);
            for (int i = 0; i < hand.Count; i++)
            {
                if (drawn == null || hand[i] != drawn.Value)
                {
                    return hand[i];
                }
            }

            throw new InvalidOperationException("Seat " + seat + " has no closed hand tile.");
        }

        public static TilePositionMap Require(TransitionResult result)
        {
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException("Transition failed: " + result.Error);
            }

            return result.RequireState();
        }

        public static TilePositionMap CreateWithoutLiveWall(TilePositionMap source)
        {
            var placements = source.AsPlacements().ToList();
            IReadOnlyList<TileId> wallTiles = source.TilesInZone(TileZone.Wall);
            for (int i = 0; i < wallTiles.Count; i++)
            {
                int index = placements.FindIndex(placement => placement.Tile == wallTiles[i]);
                placements[index] = new TilePlacement(wallTiles[i], TilePosition.Discard(0, i));
            }

            return TilePositionMap.FromPlacements(
                placements,
                source.WallPointer,
                source.WallEndExclusive,
                source.RinshanPointer);
        }
    }

    public sealed class TilePositionMapTests
    {
        [Fact]
        public void TileIdZeroAndMaxMapToExpectedTypeAndCopy()
        {
            var zero = new TileId(0);
            Assert.Equal(0, zero.TypeIndex);
            Assert.Equal(0, zero.CopyIndex);

            var max = new TileId(135);
            Assert.Equal(33, max.TypeIndex);
            Assert.Equal(3, max.CopyIndex);
        }

        [Fact]
        public void TypeAndCopyUseShiftAndMaskAcrossAllTiles()
        {
            for (int value = 0; value <= 135; value++)
            {
                var tile = new TileId(value);
                Assert.Equal(value >> 2, tile.TypeIndex);
                Assert.Equal(value & 3, tile.CopyIndex);
                Assert.Equal(value, tile.Value);
                Assert.Equal(tile, TileId.FromTypeIndex(tile.TypeIndex, tile.CopyIndex));
            }
        }

        [Fact]
        public void AllContainsEachPhysicalIdentityAndTypeGroup()
        {
            Assert.Equal(136, TileId.All.Count);
            Assert.Equal(136, TileId.All.Select(tile => tile.Value).Distinct().Count());
            Assert.Equal(34, TileId.All.Select(tile => tile.TypeIndex).Distinct().Count());
            Assert.All(
                Enumerable.Range(0, 34),
                typeIndex => Assert.Equal(4, TileId.All.Count(tile => tile.TypeIndex == typeIndex)));
        }

        [Fact]
        public void RedFivesAreExactlyTheThreeNumberFiveCopyThreeTiles()
        {
            TileId[] red = TileId.All.Where(tile => tile.IsRedFive).OrderBy(tile => tile.Value).ToArray();
            Assert.Equal(3, red.Length);
            Assert.Equal(new[] { new TileId(19), new TileId(55), new TileId(91) }, red);
            Assert.Equal(TileSuit.Man, red[0].Suit);
            Assert.Equal(TileSuit.Sou, red[1].Suit);
            Assert.Equal(TileSuit.Pin, red[2].Suit);
        }

        [Fact]
        public void CopyThreeOfNonFiveAndHonorsIsNotRed()
        {
            Assert.False(TileId.FromTypeIndex(0, 3).IsRedFive);
            Assert.False(TileId.FromTypeIndex(8, 3).IsRedFive);
            Assert.False(TileId.FromTypeIndex(27, 3).IsRedFive);
            Assert.False(TileId.FromTypeIndex(33, 3).IsRedFive);
            Assert.True(TileId.FromTypeIndex(4, 0).IsNumberTile);
            Assert.False(TileId.FromTypeIndex(4, 0).IsHonor);
            Assert.True(TileId.FromTypeIndex(31, 0).IsHonor);
        }

        [Fact]
        public void TypeIndexSuitAndRankFollowV4Order()
        {
            Assert.Equal((TileSuit.Man, 1), (new TileId(0).Suit, new TileId(0).Rank));
            Assert.Equal((TileSuit.Man, 9), (new TileId(32).Suit, new TileId(32).Rank));
            Assert.Equal((TileSuit.Sou, 1), (new TileId(36).Suit, new TileId(36).Rank));
            Assert.Equal((TileSuit.Sou, 9), (new TileId(68).Suit, new TileId(68).Rank));
            Assert.Equal((TileSuit.Pin, 1), (new TileId(72).Suit, new TileId(72).Rank));
            Assert.Equal((TileSuit.Pin, 9), (new TileId(104).Suit, new TileId(104).Rank));
            Assert.Equal((TileSuit.Wind, 1), (new TileId(108).Suit, new TileId(108).Rank));
            Assert.Equal((TileSuit.Wind, 4), (new TileId(120).Suit, new TileId(120).Rank));
            Assert.Equal((TileSuit.Dragon, 1), (new TileId(124).Suit, new TileId(124).Rank));
            Assert.Equal((TileSuit.Dragon, 3), (new TileId(132).Suit, new TileId(132).Rank));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(136)]
        [InlineData(int.MinValue)]
        [InlineData(int.MaxValue)]
        public void OutOfRangeTileValueIsRejected(int value)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TileId(value));
            Assert.False(TileId.TryFromValue(value, out _));
        }

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(34, 0)]
        [InlineData(0, -1)]
        [InlineData(0, 4)]
        public void InvalidTypeOrCopyIsRejected(int typeIndex, int copyIndex)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TileId.FromTypeIndex(typeIndex, copyIndex));
            Assert.False(TileId.TryFromTypeIndex(typeIndex, copyIndex, out _));
        }

        [Fact]
        public void DefaultPositionIsUnassigned()
        {
            TilePosition position = default;
            Assert.Equal(TileZone.Unassigned, position.Zone);
            Assert.Equal(-1, position.Seat);
            Assert.Equal(-1, position.Index);
            Assert.False(position.IsAssigned);
            Assert.Equal("UNASSIGNED", position.ToString());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(121)]
        public void WallSlotsAreValid(int index)
        {
            TilePosition position = TilePosition.Wall(index);
            Assert.Equal(TileZone.Wall, position.Zone);
            Assert.Equal(-1, position.Seat);
            Assert.Equal(index, position.Index);
        }

        [Fact]
        public void ZoneSlotBoundsAndSeatOwnershipAreEnforced()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TilePosition.Wall(122));
            Assert.Throws<ArgumentOutOfRangeException>(() => TilePosition.Wall(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => TilePosition.DeadWall(14));
            Assert.Throws<ArgumentOutOfRangeException>(() => TilePosition.DeadWallReplacement(4));
            Assert.Throws<ArgumentOutOfRangeException>(() => TilePosition.Hand(0, 14));
            Assert.Throws<ArgumentOutOfRangeException>(() => TilePosition.Meld(0, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() => TilePosition.Sleeve(0, 14));
            Assert.Throws<ArgumentOutOfRangeException>(() => TilePosition.Hand(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => TilePosition.Hand(4, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => TilePosition.Discard(4, 0));
            Assert.Equal("HAND:3:13", TilePosition.Hand(3, 13).ToString());
        }

        [Fact]
        public void PlacementMapRejectsDuplicateTileMissingTileAndDuplicatePosition()
        {
            TilePositionMap state = TestTables.Opening();
            List<TilePlacement> placements = state.AsPlacements().ToList();

            var duplicateTile = new List<TilePlacement>(placements);
            duplicateTile.Add(duplicateTile[0]);
            Assert.Throws<ArgumentException>(() =>
                TilePositionMap.FromPlacements(duplicateTile, 53, 122, 0));

            var missingTile = new List<TilePlacement>(placements);
            missingTile.RemoveAt(0);
            Assert.Throws<ArgumentException>(() =>
                TilePositionMap.FromPlacements(missingTile, 53, 122, 0));

            var duplicatePosition = new List<TilePlacement>(placements);
            duplicatePosition[1] = new TilePlacement(duplicatePosition[1].Tile, duplicatePosition[0].Position);
            Assert.Throws<ArgumentException>(() =>
                TilePositionMap.FromPlacements(duplicatePosition, 53, 122, 0));
        }

        [Fact]
        public void UnassignedStandardSetAllowsRepeatedUnassignedPositions()
        {
            TilePositionMap state = TilePositionMap.CreateUnassigned();
            Assert.Equal(0, state.CountAt(TileZone.Wall));
            Assert.Equal(136, state.CountAt(TileZone.Unassigned));
            Assert.True(TilePositionMap.Validate(state).IsValid);
        }

        [Fact]
        public void AsPlacementsIsReadOnlyAndInTileIdOrder()
        {
            TilePositionMap state = TestTables.Opening();
            IReadOnlyList<TilePlacement> placements = state.AsPlacements();
            for (int i = 0; i < placements.Count; i++)
            {
                Assert.Equal(i, placements[i].Tile.Value);
            }

            Assert.IsNotType<TilePlacement[]>(placements);
            Assert.Throws<NotSupportedException>(() =>
                ((IList<TilePlacement>)placements).Add(placements[0]));
        }

        [Fact]
        public void OpeningHasExactly136MutuallyExclusiveSlots()
        {
            TilePositionMap state = TestTables.Opening();

            Assert.Equal(136, state.CountAt(TileZone.Hand) + state.CountAt(TileZone.Wall) +
                              state.CountAt(TileZone.DeadWall) + state.CountAt(TileZone.Discard) +
                              state.CountAt(TileZone.Meld) + state.CountAt(TileZone.Sleeve) +
                              state.CountAt(TileZone.DeadWallReplacement) +
                              state.CountAt(TileZone.Unassigned));
            Assert.Equal(53, state.CountAt(TileZone.Hand));
            Assert.Equal(69, state.CountAt(TileZone.Wall));
            Assert.Equal(14, state.CountAt(TileZone.DeadWall));
            Assert.Equal(0, state.CountAt(TileZone.Discard));
            Assert.Equal(0, state.CountAt(TileZone.Meld));
            Assert.Equal(0, state.CountAt(TileZone.Unassigned));
            Assert.Equal(14, state.CountAt(TileZone.Hand, 0));
            for (int seat = 1; seat < 4; seat++)
            {
                Assert.Equal(13, state.CountAt(TileZone.Hand, seat));
            }

            Assert.Equal(53, state.WallPointer);
            Assert.Equal(122, state.WallEndExclusive);
            Assert.Equal(0, state.RinshanPointer);
            Assert.Equal(0, state.ReplacementCount);
            Assert.Equal(69, state.WallRemaining);
            Assert.Null(state.LastDiscard);
            Assert.Null(state.LastRinshanDraw);
            Assert.NotNull(state.DrawnTile);
            Assert.Equal(DrawSource.DealExtra, state.DrawnTile!.Value.Source);
            Assert.Equal(0, state.DrawnTile.Value.Seat);
            Assert.Equal(TestTables.HandTile(state, 0, 13), state.DrawnTile.Value.Tile);
            Assert.Single(state.DoraIndicators);
            Assert.Equal(TestTables.TileAtDeadWall(state, 4), state.DoraIndicators[0]);
            Assert.Equal(
                136,
                state.AsPlacements().Select(placement => placement.Position).Distinct().Count());
            Assert.True(TilePositionMap.Validate(state).IsValid);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void EveryDealerReceivesFourteenAndTheOthersThirteen(int dealerSeat)
        {
            TilePositionMap state = TestTables.Opening(dealerSeat);
            Assert.Equal(14, state.CountAt(TileZone.Hand, dealerSeat));
            for (int seat = 0; seat < 4; seat++)
            {
                if (seat != dealerSeat)
                {
                    Assert.Equal(13, state.CountAt(TileZone.Hand, seat));
                }
            }

            Assert.Equal(69, state.WallRemaining);
            Assert.True(TilePositionMap.Validate(state).IsValid);
        }

        [Fact]
        public void PreDealWallAndDealInitialProduceTheOpeningMap()
        {
            TilePositionMap preDeal = TilePositionMap.CreateWall(RecordedRandomDraw.Standard("predeal"));
            Assert.Equal(122, preDeal.CountAt(TileZone.Wall));
            Assert.Equal(14, preDeal.CountAt(TileZone.DeadWall));
            Assert.Equal(0, preDeal.WallPointer);
            Assert.Equal(122, preDeal.WallRemaining);

            TransitionResult dealt = preDeal.DealInitial(0);
            Assert.True(dealt.IsSuccess);
            TilePositionMap state = dealt.RequireState();
            Assert.Equal(1, state.Revision);
            Assert.Equal(53, state.WallPointer);
            Assert.Equal(69, state.WallRemaining);
            Assert.Equal(14, state.CountAt(TileZone.Hand, 0));
            Assert.Equal(13, state.CountAt(TileZone.Hand, 1));
            Assert.True(TilePositionMap.Validate(state).IsValid);
        }

        [Fact]
        public void DrawAndDiscardMoveTheExactPhysicalTileAndUpdateOpenDiscard()
        {
            TilePositionMap state = TestTables.Opening();
            TileId expected = TestTables.TileAtWall(state, 53);

            TransitionResult draw = state.DrawFromWall(1);
            Assert.True(draw.IsSuccess);
            Assert.Equal(expected, draw.ResultTile);
            TilePositionMap drawn = draw.RequireState();
            Assert.Equal(TilePosition.Hand(1, 13), drawn.PositionOf(expected));
            Assert.Equal(54, drawn.WallPointer);
            Assert.Equal(68, drawn.WallRemaining);
            Assert.Null(drawn.LastDiscard);
            Assert.NotNull(drawn.DrawnTile);
            Assert.Equal(expected, drawn.DrawnTile!.Value.Tile);
            Assert.Equal(DrawSource.Wall, drawn.DrawnTile.Value.Source);

            TransitionResult discard = drawn.Discard(1, expected);
            Assert.True(discard.IsSuccess);
            TilePositionMap discarded = discard.RequireState();
            Assert.Equal(TilePosition.Discard(1, 0), discarded.PositionOf(expected));
            Assert.Equal(expected, discarded.LastDiscard!.Value.Tile);
            Assert.Equal(1, discarded.LastDiscard.Value.Seat);
            Assert.Null(discarded.DrawnTile);
            Assert.Equal(13, discarded.CountAt(TileZone.Hand, 1));
            Assert.True(TilePositionMap.Validate(discarded).IsValid);
        }

        [Fact]
        public void DealerWithFourteenTilesCannotDrawAndFailureHasNoSideEffect()
        {
            TilePositionMap state = TestTables.Opening();
            ulong before = state.StateHash;
            int beforeRevision = state.Revision;

            TransitionResult result = state.DrawFromWall(0);

            Assert.False(result.IsSuccess);
            Assert.Equal(TransitionErrorCode.HandFull, result.Error!.Code);
            Assert.Null(result.State);
            Assert.Equal(before, state.StateHash);
            Assert.Equal(beforeRevision, state.Revision);
            Assert.Equal(14, state.CountAt(TileZone.Hand, 0));
        }

        [Fact]
        public void ChiMovesTwoHandTilesAndTheOpenDiscardIntoOneMeld()
        {
            RecordedRandomDraw order = TestTables.Crafted(
                (0, new TileId(8)),
                (4, new TileId(0)),
                (5, new TileId(4)));
            TilePositionMap opened = TestTables.Require(
                TilePositionMap.CreateDealt(order, 0).Discard(0, new TileId(8)));

            TransitionResult chi = opened.Chi(1, 0, new TileId(8), new TileId(0), new TileId(4));

            Assert.True(chi.IsSuccess);
            TilePositionMap state = chi.RequireState();
            Assert.Equal(0, state.CountAt(TileZone.Discard, 0));
            Assert.Equal(3, state.CountAt(TileZone.Meld, 1));
            Assert.Equal(11, state.CountAt(TileZone.Hand, 1));
            Assert.Null(state.LastDiscard);
            Assert.True(TilePositionMap.Validate(state).IsValid);
        }

        [Fact]
        public void PonMovesThreeDistinctPhysicalCopies()
        {
            RecordedRandomDraw order = TestTables.Crafted(
                (0, new TileId(19)),
                (4, new TileId(16)),
                (5, new TileId(17)));
            TilePositionMap opened = TestTables.Require(
                TilePositionMap.CreateDealt(order, 0).Discard(0, new TileId(19)));

            TransitionResult pon = opened.Pon(1, 0, new TileId(19), new TileId(16), new TileId(17));

            Assert.True(pon.IsSuccess);
            TilePositionMap state = pon.RequireState();
            Assert.Equal(3, state.CountAt(TileZone.Meld, 1));
            Assert.Equal(0, state.CountAt(TileZone.Discard, 0));
            Assert.Equal(11, state.CountAt(TileZone.Hand, 1));
            Assert.Null(state.LastDiscard);
            Assert.True(TilePositionMap.Validate(state).IsValid);
        }

        [Fact]
        public void OpenKanMovesThreeHandTilesAndTheCalledDiscard()
        {
            RecordedRandomDraw order = TestTables.Crafted(
                (0, new TileId(19)),
                (4, new TileId(16)),
                (5, new TileId(17)),
                (6, new TileId(18)));
            TilePositionMap opened = TestTables.Require(
                TilePositionMap.CreateDealt(order, 0).Discard(0, new TileId(19)));

            TransitionResult kan = opened.OpenKan(
                1,
                0,
                new TileId(19),
                new TileId(16),
                new TileId(17),
                new TileId(18));

            Assert.True(kan.IsSuccess);
            TilePositionMap state = kan.RequireState();
            Assert.Equal(4, state.CountAt(TileZone.Meld, 1));
            Assert.Equal(10, state.CountAt(TileZone.Hand, 1));
            Assert.Equal(0, state.CountAt(TileZone.Discard, 0));
            Assert.Equal(1, state.KanCount);
            Assert.Null(state.LastDiscard);
            Assert.True(TilePositionMap.Validate(state).IsValid);
        }

        [Fact]
        public void ClosedKanMovesFourHandTiles()
        {
            RecordedRandomDraw order = TestTables.Crafted(
                (4, new TileId(16)),
                (5, new TileId(17)),
                (6, new TileId(18)),
                (7, new TileId(19)));
            TilePositionMap state = TilePositionMap.CreateDealt(order, 0);

            TransitionResult kan = state.ClosedKan(1, new TileId(16), new TileId(17), new TileId(18), new TileId(19));

            Assert.True(kan.IsSuccess);
            TilePositionMap after = kan.RequireState();
            Assert.Equal(4, after.CountAt(TileZone.Meld, 1));
            Assert.Equal(9, after.CountAt(TileZone.Hand, 1));
            Assert.Equal(1, after.KanCount);
            Assert.True(TilePositionMap.Validate(after).IsValid);
        }

        [Fact]
        public void AddedKanConsumesTheFourthCopyIntoTheExistingPonGroup()
        {
            RecordedRandomDraw order = TestTables.Crafted(
                (0, new TileId(19)),
                (4, new TileId(16)),
                (5, new TileId(17)),
                (6, new TileId(18)));
            TilePositionMap opened = TestTables.Require(
                TilePositionMap.CreateDealt(order, 0).Discard(0, new TileId(19)));
            TilePositionMap pon = TestTables.Require(
                opened.Pon(1, 0, new TileId(19), new TileId(16), new TileId(17)));

            TransitionResult added = pon.AddedKan(1, new TileId(18));

            Assert.True(added.IsSuccess);
            TilePositionMap state = added.RequireState();
            Assert.Equal(4, state.CountAt(TileZone.Meld, 1));
            Assert.Equal(10, state.CountAt(TileZone.Hand, 1));
            Assert.Equal(1, state.KanCount);
            Assert.True(TilePositionMap.Validate(state).IsValid);
        }

        [Fact]
        public void SwapHandWithWallIsAnEqualPhysicalExchangeAndKeepsCursor()
        {
            TilePositionMap state = TestTables.Opening();
            TileId handTile = TestTables.HandTile(state, 1, 0);
            TileId wallTile = TestTables.TileAtWall(state, 53);
            TilePosition originalHand = state.PositionOf(handTile);

            TransitionResult swap = state.SwapHandWithWall(1, handTile, 53);

            Assert.True(swap.IsSuccess);
            Assert.Equal(wallTile, swap.ResultTile);
            TilePositionMap after = swap.RequireState();
            Assert.Equal(TilePosition.Wall(53), after.PositionOf(handTile));
            Assert.Equal(originalHand, after.PositionOf(wallTile));
            Assert.Equal(53, after.WallPointer);
            Assert.Equal(69, after.WallRemaining);
            Assert.True(TilePositionMap.Validate(after).IsValid);
        }

        [Fact]
        public void RinshanDrawReplenishesTheDeadWallFromTheLiveWallTail()
        {
            TilePositionMap state = TestTables.Opening();
            TileId expectedRinshan = TestTables.TileAtDeadWall(state, 0);
            TileId expectedReplacement = TestTables.TileAtWall(state, 121);

            TransitionResult draw = state.DrawReplacement(1);

            Assert.True(draw.IsSuccess);
            Assert.Equal(expectedRinshan, draw.ResultTile);
            TilePositionMap after = draw.RequireState();
            Assert.Equal(TilePosition.Hand(1, 13), after.PositionOf(expectedRinshan));
            Assert.True(after.IsPositionFree(TilePosition.DeadWall(0)));
            Assert.Equal(TilePosition.DeadWallReplacement(0), after.PositionOf(expectedReplacement));
            Assert.Equal(1, after.RinshanPointer);
            Assert.Equal(1, after.ReplacementCount);
            Assert.Equal(13, after.CountAt(TileZone.DeadWall));
            Assert.Equal(121, after.WallEndExclusive);
            Assert.Equal(68, after.WallRemaining);
            Assert.Null(after.LastDiscard);
            Assert.NotNull(after.LastRinshanDraw);
            Assert.Equal(expectedRinshan, after.LastRinshanDraw!.Value.Tile);
            Assert.Equal(1, after.LastRinshanDraw.Value.Seat);
            Assert.Equal(0, after.LastRinshanDraw.Value.DeadWallIndex);
            Assert.NotNull(after.DrawnTile);
            Assert.Equal(expectedRinshan, after.DrawnTile!.Value.Tile);
            Assert.Equal(DrawSource.Rinshan, after.DrawnTile.Value.Source);
            Assert.True(TilePositionMap.Validate(after).IsValid);
        }

        [Fact]
        public void SwapDrawnWithNextRinshanMovesTheNextDeadWallTileIntoHand()
        {
            TilePositionMap state = TestTables.Opening();
            TileId drawn = TestTables.TileAtDeadWall(state, 0);
            TileId next = TestTables.TileAtDeadWall(state, 1);
            TilePositionMap afterRinshan = TestTables.Require(state.DrawReplacement(1));

            TransitionResult swap = afterRinshan.SwapDrawnWithNextRinshan(1, drawn);

            Assert.True(swap.IsSuccess);
            Assert.Equal(next, swap.ResultTile);
            TilePositionMap after = swap.RequireState();
            Assert.Equal(TilePosition.DeadWall(1), after.PositionOf(drawn));
            Assert.Equal(TilePosition.Hand(1, 13), after.PositionOf(next));
            Assert.True(after.IsPositionFree(TilePosition.DeadWall(0)));
            Assert.Null(after.LastRinshanDraw);
            Assert.NotNull(after.DrawnTile);
            Assert.Equal(next, after.DrawnTile!.Value.Tile);
            Assert.Equal(DrawSource.Rinshan, after.DrawnTile.Value.Source);
            Assert.True(TilePositionMap.Validate(after).IsValid);

            TransitionResult repeated = after.SwapDrawnWithNextRinshan(1, drawn);
            Assert.False(repeated.IsSuccess);
            Assert.Equal(TransitionErrorCode.RinshanDrawEvidenceMismatch, repeated.Error!.Code);
        }

        [Fact]
        public void RinshanSwapRejectsAnUnrecordedHandTileWithZeroSideEffect()
        {
            TilePositionMap afterRinshan = TestTables.Require(
                TestTables.Opening().DrawReplacement(1));
            TileId recorded = afterRinshan.LastRinshanDraw!.Value.Tile;
            TileId other = TestTables.HandTile(afterRinshan, 1, 0);
            Assert.NotEqual(recorded, other);
            ulong beforeHash = afterRinshan.StateHash;
            int beforeRevision = afterRinshan.Revision;
            TilePosition before = afterRinshan.PositionOf(other);

            TransitionResult result = afterRinshan.SwapDrawnWithNextRinshan(1, other);

            Assert.False(result.IsSuccess);
            Assert.Equal(TransitionErrorCode.RinshanDrawEvidenceMismatch, result.Error!.Code);
            Assert.Null(result.State);
            Assert.Equal(beforeHash, afterRinshan.StateHash);
            Assert.Equal(beforeRevision, afterRinshan.Revision);
            Assert.Equal(before, afterRinshan.PositionOf(other));
            Assert.NotNull(afterRinshan.LastRinshanDraw);
        }

        [Fact]
        public void DiscardInsteadOfSwapConsumesTheRinshanEvidence()
        {
            TilePositionMap afterRinshan = TestTables.Require(
                TestTables.Opening().DrawReplacement(1));
            TileId recorded = afterRinshan.LastRinshanDraw!.Value.Tile;
            TilePositionMap afterDiscard = TestTables.Require(afterRinshan.Discard(1, recorded));
            Assert.Null(afterDiscard.LastRinshanDraw);

            TransitionResult stale = afterDiscard.SwapDrawnWithNextRinshan(1, recorded);

            Assert.False(stale.IsSuccess);
            Assert.Equal(TransitionErrorCode.RinshanDrawEvidenceMismatch, stale.Error!.Code);
            Assert.Null(stale.State);
        }

        [Fact]
        public void NormalDrawConsumesTheRinshanEvidence()
        {
            TilePositionMap opening = TestTables.Opening();
            TileId discarded = TestTables.HandTile(opening, 1, 0);
            TilePositionMap twelveTiles = TestTables.Require(opening.Discard(1, discarded));
            TilePositionMap afterRinshan = TestTables.Require(twelveTiles.DrawReplacement(1));
            TileId recorded = afterRinshan.LastRinshanDraw!.Value.Tile;

            TilePositionMap afterNormalDraw = TestTables.Require(afterRinshan.DrawFromWall(1));
            Assert.Null(afterNormalDraw.LastRinshanDraw);

            TransitionResult stale = afterNormalDraw.SwapDrawnWithNextRinshan(1, recorded);

            Assert.False(stale.IsSuccess);
            Assert.Equal(TransitionErrorCode.RinshanDrawEvidenceMismatch, stale.Error!.Code);
            Assert.Null(stale.State);
        }

        [Fact]
        public void AnotherSwapConsumesTheRinshanEvidence()
        {
            TilePositionMap afterRinshan = TestTables.Require(
                TestTables.Opening().DrawReplacement(1));
            TileId recorded = afterRinshan.LastRinshanDraw!.Value.Tile;
            TileId other = TestTables.HandTile(afterRinshan, 1, 0);
            TilePositionMap afterSwap = TestTables.Require(
                afterRinshan.SwapHandWithWall(1, other, 53));
            Assert.Null(afterSwap.LastRinshanDraw);

            TransitionResult stale = afterSwap.SwapDrawnWithNextRinshan(1, recorded);

            Assert.False(stale.IsSuccess);
            Assert.Equal(TransitionErrorCode.RinshanDrawEvidenceMismatch, stale.Error!.Code);
            Assert.Null(stale.State);
        }

        [Fact]
        public void BeamSwapCannotTakeARevealedDoraIndicator()
        {
            TilePositionMap state = TestTables.Opening();
            Assert.Single(state.DoraIndicators);
            TileId indicator = TestTables.TileAtDeadWall(state, 4);
            Assert.Contains(indicator, state.DoraIndicators);
            TileId handTile = TestTables.HandTile(state, 1, 0);
            ulong beforeHash = state.StateHash;
            int beforeRevision = state.Revision;

            TransitionResult result = state.SwapHandWithDeadWall(1, handTile, 4);

            Assert.False(result.IsSuccess);
            Assert.Equal(TransitionErrorCode.InvalidPosition, result.Error!.Code);
            Assert.Null(result.State);
            Assert.Equal(beforeHash, state.StateHash);
            Assert.Equal(beforeRevision, state.Revision);
            Assert.Equal(TilePosition.DeadWall(4), state.PositionOf(indicator));
        }

        [Fact]
        public void WallSwapUpdatesDrawnEvidenceOnlyWhenTheDrawnTileMoves()
        {
            TilePositionMap state = TestTables.Opening(0);
            DrawnTileEvidence drawn = state.DrawnTile!.Value;
            TileId wallTile = TestTables.TileAtWall(state, 53);

            TilePositionMap afterDrawnSwap = TestTables.Require(
                state.SwapHandWithWall(0, drawn.Tile, 53));
            Assert.NotNull(afterDrawnSwap.DrawnTile);
            Assert.Equal(wallTile, afterDrawnSwap.DrawnTile!.Value.Tile);
            Assert.Equal(0, afterDrawnSwap.DrawnTile.Value.Seat);

            TileId closed = TestTables.ClosedHandTile(state, 0);
            TilePositionMap afterClosedSwap = TestTables.Require(
                state.SwapHandWithWall(0, closed, 53));
            Assert.Equal(state.DrawnTile, afterClosedSwap.DrawnTile);
        }

        [Fact]
        public void BeamSwapRejectsDealerExtraDrawnTile()
        {
            TilePositionMap state = TestTables.Opening(0);
            DrawnTileEvidence drawn = state.DrawnTile!.Value;
            Assert.Equal(0, drawn.Seat);
            Assert.Equal(DrawSource.DealExtra, drawn.Source);
            ulong beforeHash = state.StateHash;
            int beforeRevision = state.Revision;
            TilePosition before = state.PositionOf(drawn.Tile);

            TransitionResult result = state.SwapHandWithDeadWall(0, drawn.Tile, 5);

            Assert.False(result.IsSuccess);
            Assert.Equal(TransitionErrorCode.DrawnTileNotClosed, result.Error!.Code);
            Assert.Null(result.State);
            Assert.Equal(beforeHash, state.StateHash);
            Assert.Equal(beforeRevision, state.Revision);
            Assert.Equal(before, state.PositionOf(drawn.Tile));
        }

        [Fact]
        public void BeamSwapRejectsNormalAndRinshanDrawnTiles()
        {
            TilePositionMap normal = TestTables.Require(TestTables.Opening().DrawFromWall(1));
            DrawnTileEvidence normalDrawn = normal.DrawnTile!.Value;
            Assert.Equal(DrawSource.Wall, normalDrawn.Source);
            ulong normalHash = normal.StateHash;
            TransitionResult normalSwap = normal.SwapHandWithDeadWall(1, normalDrawn.Tile, 5);
            Assert.False(normalSwap.IsSuccess);
            Assert.Equal(TransitionErrorCode.DrawnTileNotClosed, normalSwap.Error!.Code);
            Assert.Equal(normalHash, normal.StateHash);

            TilePositionMap rinshan = TestTables.Require(TestTables.Opening().DrawReplacement(1));
            DrawnTileEvidence rinshanDrawn = rinshan.DrawnTile!.Value;
            Assert.Equal(DrawSource.Rinshan, rinshanDrawn.Source);
            ulong rinshanHash = rinshan.StateHash;
            int rinshanRevision = rinshan.Revision;
            TilePosition rinshanPosition = rinshan.PositionOf(rinshanDrawn.Tile);

            TransitionResult deadSwap = rinshan.SwapHandWithDeadWall(1, rinshanDrawn.Tile, 5);
            Assert.False(deadSwap.IsSuccess);
            Assert.Equal(TransitionErrorCode.DrawnTileNotClosed, deadSwap.Error!.Code);
            Assert.Null(deadSwap.State);
            Assert.Equal(rinshanHash, rinshan.StateHash);
            Assert.Equal(rinshanRevision, rinshan.Revision);
            Assert.Equal(rinshanPosition, rinshan.PositionOf(rinshanDrawn.Tile));

            TransitionResult replacementSwap = rinshan.SwapHandWithReplacement(1, rinshanDrawn.Tile, 0);
            Assert.False(replacementSwap.IsSuccess);
            Assert.Equal(TransitionErrorCode.DrawnTileNotClosed, replacementSwap.Error!.Code);
            Assert.Null(replacementSwap.State);
            Assert.Equal(rinshanHash, rinshan.StateHash);
            Assert.Equal(rinshanRevision, rinshan.Revision);
            Assert.Equal(rinshanPosition, rinshan.PositionOf(rinshanDrawn.Tile));
        }

        [Fact]
        public void BeamSwapAllowsAClosedHandTileForDeadWallAndReplacement()
        {
            TilePositionMap rinshan = TestTables.Require(TestTables.Opening().DrawReplacement(1));
            TileId closed = TestTables.ClosedHandTile(rinshan, 1);
            Assert.NotEqual(rinshan.DrawnTile!.Value.Tile, closed);

            TransitionResult deadSwap = rinshan.SwapHandWithDeadWall(1, closed, 5);
            Assert.True(deadSwap.IsSuccess);
            Assert.True(TilePositionMap.Validate(deadSwap.State!).IsValid);

            TransitionResult replacementSwap = rinshan.SwapHandWithReplacement(1, closed, 0);
            Assert.True(replacementSwap.IsSuccess);
            Assert.True(TilePositionMap.Validate(replacementSwap.State!).IsValid);
        }

        [Fact]
        public void BeamSwapDeadWallAndReplacementExchangesArePhysical()
        {
            TilePositionMap state = TestTables.Opening();
            TileId handTile = TestTables.HandTile(state, 1, 0);
            TileId deadTile = TestTables.TileAtDeadWall(state, 5);
            TilePosition handPosition = state.PositionOf(handTile);

            TransitionResult deadSwap = state.SwapHandWithDeadWall(1, handTile, 5);
            Assert.True(deadSwap.IsSuccess);
            TilePositionMap afterDead = deadSwap.RequireState();
            Assert.Equal(TilePosition.DeadWall(5), afterDead.PositionOf(handTile));
            Assert.Equal(handPosition, afterDead.PositionOf(deadTile));

            TilePositionMap afterRinshan = TestTables.Require(state.DrawReplacement(1));
            TileId replacementTile = TestTables.TileAtReplacement(afterRinshan, 0);
            TileId replacementHandTile = TestTables.HandTile(afterRinshan, 1, 0);
            TilePosition replacementHandPosition = afterRinshan.PositionOf(replacementHandTile);

            TransitionResult replacementSwap = afterRinshan.SwapHandWithReplacement(1, replacementHandTile, 0);
            Assert.True(replacementSwap.IsSuccess);
            TilePositionMap afterReplacement = replacementSwap.RequireState();
            Assert.Equal(TilePosition.DeadWallReplacement(0), afterReplacement.PositionOf(replacementHandTile));
            Assert.Equal(replacementHandPosition, afterReplacement.PositionOf(replacementTile));
            Assert.True(TilePositionMap.Validate(afterReplacement).IsValid);
        }

        [Fact]
        public void ConsumedLatestDiscardCannotLetAnOlderRiverTileBecomeCallable()
        {
            RecordedRandomDraw order = TestTables.Crafted(
                (0, new TileId(0)),
                (1, new TileId(4)),
                (4, new TileId(5)),
                (5, new TileId(8)),
                (6, new TileId(9)),
                (7, new TileId(12)));
            TilePositionMap state = TilePositionMap.CreateDealt(order, 0);
            state = TestTables.Require(state.Discard(0, new TileId(0)));
            state = TestTables.Require(state.Discard(0, new TileId(4)));
            Assert.Equal(new TileId(4), state.LastDiscard!.Value.Tile);

            TilePositionMap afterChi = TestTables.Require(
                state.Chi(1, 0, new TileId(4), new TileId(8), new TileId(12)));
            Assert.Null(afterChi.LastDiscard);
            Assert.Equal(TilePosition.Discard(0, 0), afterChi.PositionOf(new TileId(0)));
            ulong before = afterChi.StateHash;
            int beforeRevision = afterChi.Revision;

            TransitionResult staleChi = afterChi.Chi(1, 0, new TileId(0), new TileId(5), new TileId(9));

            Assert.False(staleChi.IsSuccess);
            Assert.Equal(TransitionErrorCode.CalledTileNotLatestDiscard, staleChi.Error!.Code);
            Assert.Null(staleChi.State);
            Assert.Equal(before, afterChi.StateHash);
            Assert.Equal(beforeRevision, afterChi.Revision);
            Assert.Equal(TilePosition.Discard(0, 0), afterChi.PositionOf(new TileId(0)));
        }

        [Fact]
        public void DrawingClosesTheDiscardCallWindow()
        {
            RecordedRandomDraw order = TestTables.Crafted(
                (0, new TileId(0)),
                (8, new TileId(1)),
                (9, new TileId(2)));
            TilePositionMap state = TilePositionMap.CreateDealt(order, 0);
            state = TestTables.Require(state.Discard(0, new TileId(0)));
            Assert.NotNull(state.LastDiscard);

            TilePositionMap afterDraw = TestTables.Require(state.DrawFromWall(1));
            Assert.Null(afterDraw.LastDiscard);

            TransitionResult stalePon = afterDraw.Pon(
                2,
                0,
                new TileId(0),
                new TileId(1),
                new TileId(2));
            Assert.False(stalePon.IsSuccess);
            Assert.Equal(TransitionErrorCode.CalledTileNotLatestDiscard, stalePon.Error!.Code);
        }

        [Fact]
        public void ForeignSeatWallSwapAndInvalidWallSlotAreRejected()
        {
            TilePositionMap state = TestTables.Opening();
            TileId foreign = TestTables.HandTile(state, 1, 0);
            ulong before = state.StateHash;

            TransitionResult discard = state.Discard(2, foreign);
            Assert.False(discard.IsSuccess);
            Assert.Equal(TransitionErrorCode.SourceMismatch, discard.Error!.Code);

            TransitionResult swap = state.SwapHandWithWall(2, foreign, 53);
            Assert.False(swap.IsSuccess);
            Assert.Equal(TransitionErrorCode.SourceMismatch, swap.Error!.Code);

            TileId own = TestTables.HandTile(state, 1, 1);
            TransitionResult outside = state.SwapHandWithWall(1, own, 122);
            Assert.False(outside.IsSuccess);
            Assert.Equal(TransitionErrorCode.WallSlotOutOfRange, outside.Error!.Code);

            TransitionResult emptySlot = state.SwapHandWithWall(1, own, 52);
            Assert.False(emptySlot.IsSuccess);
            Assert.Equal(TransitionErrorCode.MissingTile, emptySlot.Error!.Code);
            Assert.Equal(before, state.StateHash);
        }

        [Fact]
        public void WrongKamichaAndDuplicateMeldCopiesAreRejected()
        {
            RecordedRandomDraw order = TestTables.Crafted(
                (0, new TileId(8)),
                (4, new TileId(0)),
                (5, new TileId(4)));
            TilePositionMap opened = TestTables.Require(
                TilePositionMap.CreateDealt(order, 0).Discard(0, new TileId(8)));

            TransitionResult wrongKamicha = opened.Chi(1, 2, new TileId(8), new TileId(0), new TileId(4));
            Assert.False(wrongKamicha.IsSuccess);
            Assert.Equal(TransitionErrorCode.InvalidMeldShape, wrongKamicha.Error!.Code);

            TransitionResult duplicateCopies = opened.Pon(1, 0, new TileId(8), new TileId(0), new TileId(0));
            Assert.False(duplicateCopies.IsSuccess);
            Assert.Equal(TransitionErrorCode.InvalidMeldShape, duplicateCopies.Error!.Code);
        }

        [Fact]
        public void DrawingWhenNoLiveWallTileRemainsIsRejected()
        {
            TilePositionMap exhausted = TestTables.CreateWithoutLiveWall(TestTables.Opening());
            Assert.Equal(0, exhausted.WallRemaining);

            TransitionResult draw = exhausted.DrawFromWall(0);
            Assert.False(draw.IsSuccess);
            Assert.Equal(TransitionErrorCode.WallExhausted, draw.Error!.Code);
            Assert.Null(draw.State);

            TransitionResult replacement = exhausted.DrawReplacement(0);
            Assert.False(replacement.IsSuccess);
            Assert.Equal(TransitionErrorCode.NoReplacementTile, replacement.Error!.Code);
            Assert.Null(replacement.State);
        }

        [Fact]
        public void FifthRinshanDrawIsRejectedAfterFourReplacements()
        {
            TilePositionMap state = TestTables.Opening();
            for (int i = 0; i < 4; i++)
            {
                TransitionResult draw = state.DrawReplacement(1);
                Assert.True(draw.IsSuccess);
                state = TestTables.Require(draw.State!.Discard(1, draw.ResultTile.GetValueOrDefault()));
            }

            Assert.Equal(4, state.RinshanPointer);
            Assert.Equal(4, state.ReplacementCount);

            TransitionResult fifth = state.DrawReplacement(1);
            Assert.False(fifth.IsSuccess);
            Assert.Equal(TransitionErrorCode.DeadWallExhausted, fifth.Error!.Code);
            Assert.Null(fifth.State);
        }

        [Fact]
        public void FifthKanIsRejectedAtTheFourKanLimit()
        {
            TilePositionMap opening = TestTables.Opening();
            TilePositionMap fourKans = TilePositionMap.FromPlacements(
                opening.AsPlacements(),
                opening.WallPointer,
                opening.WallEndExclusive,
                opening.RinshanPointer,
                null,
                0,
                null,
                opening.DoraIndicators,
                4);
            Assert.Equal(4, fourKans.KanCount);
            ulong beforeHash = fourKans.StateHash;
            int beforeRevision = fourKans.Revision;

            TransitionResult result = fourKans.ClosedKan(
                1,
                new TileId(16),
                new TileId(17),
                new TileId(18),
                new TileId(19));

            Assert.False(result.IsSuccess);
            Assert.Equal(TransitionErrorCode.MeldSlotExhausted, result.Error!.Code);
            Assert.Null(result.State);
            Assert.Equal(beforeHash, fourKans.StateHash);
            Assert.Equal(beforeRevision, fourKans.Revision);
        }

        [Fact]
        public void PublicApiDoesNotExposeGenericMoveBypass()
        {
            MethodInfo[] publicMethods = typeof(TilePositionMap)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

            Assert.DoesNotContain(publicMethods, method => method.Name == "ApplyMoves");
            Assert.DoesNotContain(publicMethods, method => method.Name == "Apply");
            Assert.DoesNotContain(
                publicMethods,
                method => method.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(IReadOnlyList<TileMove>)));
        }

        [Fact]
        public void DomainAssemblyDoesNotReferenceContractsUnityNetworkOrDatabase()
        {
            string[] referenced = typeof(TilePositionMap).Assembly
                .GetReferencedAssemblies()
                .Select(name => name.Name ?? string.Empty)
                .ToArray();
            string[] forbidden =
            {
                "Mahjong.Contracts",
                "Mahjong.Client",
                "Mahjong.Server",
                "Mahjong.TableRuntime",
                "Mahjong.LocalHost",
                "UnityEngine",
                "UnityEditor",
                "System.Net",
                "System.Data",
                "Microsoft.Data"
            };

            for (int i = 0; i < referenced.Length; i++)
            {
                for (int j = 0; j < forbidden.Length; j++)
                {
                    Assert.False(
                        referenced[i].IndexOf(forbidden[j], StringComparison.OrdinalIgnoreCase) >= 0,
                        "Domain references forbidden assembly " + referenced[i] + ".");
                }
            }

            Assert.Equal("Mahjong.Domain", typeof(TileId).Assembly.GetName().Name);
        }

        [Fact]
        public void ConservationReportCountsEveryPhysicalTile()
        {
            TilePositionMap state = TestTables.Opening();
            ConservationReport report = TilePositionMap.Validate(state);
            Assert.True(report.IsValid);
            Assert.Equal(136, report.AssignedCount);
            Assert.Equal(0, report.UnassignedCount);
            Assert.Equal(14, report.DeadWallCount);
            Assert.Equal(0, report.ReplacementCount);
            Assert.Equal(state.StateHash, report.StateHash);
            Assert.Contains("OK", report.ToString());
        }

        [Fact]
        public void InvalidPointersAndPartialUnassignmentAreRejectedAtConstruction()
        {
            TilePositionMap state = TestTables.Opening();
            Assert.Throws<ConservationException>(() =>
                TilePositionMap.FromPlacements(state.AsPlacements(), 0, 123, 0));
            Assert.Throws<ConservationException>(() =>
                TilePositionMap.FromPlacements(state.AsPlacements(), 0, -1, 0));
            Assert.Throws<ConservationException>(() =>
                TilePositionMap.FromPlacements(state.AsPlacements(), 0, 122, 5));
            Assert.Throws<ConservationException>(() =>
                TilePositionMap.FromPlacements(state.AsPlacements(), 123, 122, 0));

            List<TilePlacement> partial = state.AsPlacements().ToList();
            partial[0] = new TilePlacement(partial[0].Tile, TilePosition.Unassigned);
            Assert.Throws<ConservationException>(() =>
                TilePositionMap.FromPlacements(partial, state.WallPointer, state.WallEndExclusive, 0));
        }

        [Fact]
        public void ReplacementWithoutRinshanProgressAndBadDiscardEvidenceAreRejected()
        {
            TilePositionMap state = TestTables.Opening();
            List<TilePlacement> placements = state.AsPlacements().ToList();
            TileId deadTile = state.TileAt(TilePosition.DeadWall(0));
            int index = placements.FindIndex(placement => placement.Tile == deadTile);
            placements[index] = new TilePlacement(deadTile, TilePosition.DeadWallReplacement(0));

            Assert.Throws<ConservationException>(() =>
                TilePositionMap.FromPlacements(placements, state.WallPointer, state.WallEndExclusive, 0));

            var badEvidence = new LastDiscardEvidence(new TileId(0), TilePosition.Discard(0, 0));
            Assert.Throws<ConservationException>(() =>
                TilePositionMap.FromPlacements(
                    state.AsPlacements(),
                    state.WallPointer,
                    state.WallEndExclusive,
                    0,
                    badEvidence));
        }
    }
}
