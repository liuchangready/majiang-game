using System;
using System.Collections.Generic;
using Mahjong.Ai;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Ai.Tests
{
    /// <summary>
    /// Behaviour matrix for the ENG-021 port of V4 <c>AiCheatHeuristics.kt</c>.
    ///
    /// Every group exercises an observable behaviour the V4 source actually has, with
    /// expectations derived by hand from the V4 semantics: the same-type count that
    /// includes the evaluated tile itself and every physical copy (+18 for 3+, +10 for
    /// exactly 2), the same-suit rank-distance neighbour bonus (5 per distance-1, 2 per
    /// distance-2, nothing at distance 3 or across suits), the terminal penalty (-2) and
    /// honor bonus (dragon +4, wind +1) with no red-five/self-wind/dora weighting, the
    /// inert <c>it !== tile</c> reference guard, Kotlin <c>take</c> limit semantics
    /// (negative throws, zero is empty, oversized returns everything, no 14-tile cap and
    /// no candidate-limit clamp), the (value, tile id) tiebreak in both sort directions,
    /// and the no-mutation / defensive-copy contract. Ordering is additionally
    /// cross-checked against the real V4 Kotlin sources by the Kotlin/C# differential
    /// probe (5,054 cases, byte-identical dumps).
    /// </summary>
    public class AiCheatHeuristicsTests
    {
        private static TileId T(int id)
        {
            return new TileId(id);
        }

        private static List<TileId> H(params int[] ids)
        {
            var hand = new List<TileId>(ids.Length);
            foreach (int id in ids)
            {
                hand.Add(new TileId(id));
            }

            return hand;
        }

        // Physical id reference: 0..3 = 1m copies, 8..11 = 3m, 12..15 = 4m, 16..19 = 5m
        // (19 is the red five), 20..23 = 6m, 24..27 = 7m, 32..35 = 9m, 36..39 = 1s,
        // 40..43 = 2s, 72..75 = 1p, 80..83 = 3p, 108..111 = East, 112..115 = South,
        // 124..127 = White, 128..131 = Green.

        // ---------------------------------------------------------------------
        // Empty hand
        // ---------------------------------------------------------------------

        [Fact]
        public void EmptyHandScoresOnlyTheTileItself()
        {
            // Number tile: no same-type bonus, no neighbours, terminal penalty applies.
            Assert.Equal(-2, AiCheatHeuristics.TileValue(T(0), H()));
            // Simple five of man: zero.
            Assert.Equal(0, AiCheatHeuristics.TileValue(T(16), H()));
            // Wind: +1. Dragon: +4.
            Assert.Equal(1, AiCheatHeuristics.TileValue(T(108), H()));
            Assert.Equal(4, AiCheatHeuristics.TileValue(T(124), H()));
        }

        [Fact]
        public void EmptyHandSortsToEmptyForEveryValidLimit()
        {
            Assert.Empty(AiCheatHeuristics.LeastValuableHandTiles(H(), 0));
            Assert.Empty(AiCheatHeuristics.LeastValuableHandTiles(H(), 5));
            Assert.Empty(AiCheatHeuristics.MostValuableHandTiles(H(), 0));
            Assert.Empty(AiCheatHeuristics.MostValuableHandTiles(H(), 5));
        }

        // ---------------------------------------------------------------------
        // Same-type counting: single / pair / triple / quad
        // ---------------------------------------------------------------------

        [Fact]
        public void SameTypeCountAwardsTenForAPairAndEighteenForThreeOrMore()
        {
            // A single 5m has no same-type bonus.
            Assert.Equal(0, AiCheatHeuristics.TileValue(T(16), H(16)));
            // Pair of fives: +10.
            Assert.Equal(10, AiCheatHeuristics.TileValue(T(16), H(16, 17)));
            // Triplet: +18.
            Assert.Equal(18, AiCheatHeuristics.TileValue(T(16), H(16, 17, 18)));
            // Quad: still +18 (no extra beyond three).
            Assert.Equal(18, AiCheatHeuristics.TileValue(T(16), H(16, 17, 18, 19)));
        }

        [Fact]
        public void SameTypeCountIncludesTheEvaluatedTileItself()
        {
            // V4 counts same-type tiles without excluding the evaluated tile, so a pair
            // is +10 even when the scored tile is one of the two copies.
            List<TileId> pair = H(16, 17);
            Assert.Equal(10, AiCheatHeuristics.TileValue(T(16), pair));
            Assert.Equal(10, AiCheatHeuristics.TileValue(T(17), pair));
            // Scored from outside the list, the same pair is still +10.
            Assert.Equal(10, AiCheatHeuristics.TileValue(T(18), pair));
        }

        // ---------------------------------------------------------------------
        // Rank-distance neighbours and suit boundaries
        // ---------------------------------------------------------------------

        [Fact]
        public void DistanceOneAddsFiveAndDistanceTwoAddsTwoPerNeighbour()
        {
            // 4m next to one 5m: distance 1 → +5.
            Assert.Equal(5, AiCheatHeuristics.TileValue(T(12), H(12, 16)));
            // 4m with two 5m copies: +10 (per-tile, not per-type).
            Assert.Equal(10, AiCheatHeuristics.TileValue(T(12), H(12, 16, 17)));
            // 4m next to one 6m: distance 2 → +2.
            Assert.Equal(2, AiCheatHeuristics.TileValue(T(12), H(12, 20)));
            // 4m next to one 7m: distance 3 → nothing.
            Assert.Equal(0, AiCheatHeuristics.TileValue(T(12), H(12, 24)));
            // 4m between one 5m and one 6m: +5 +2.
            Assert.Equal(7, AiCheatHeuristics.TileValue(T(12), H(12, 16, 20)));
        }

        [Fact]
        public void NeighboursNeverCrossSuits()
        {
            // 9m (id 32) and 1s (id 36) are adjacent types in the 34-dimension order but
            // different suits: no distance bonus, no same-type bonus.
            Assert.Equal(-2, AiCheatHeuristics.TileValue(T(32), H(32, 36)));
            // 4m beside 2s (distance 1 in rank, different suit): no bonus.
            Assert.Equal(0, AiCheatHeuristics.TileValue(T(12), H(12, 40)));
            // Three-suit boundary hand stays disjoint: 9m, 1s, 1p each score alone.
            List<TileId> boundary = H(32, 36, 72);
            Assert.Equal(-2, AiCheatHeuristics.TileValue(T(32), boundary));
            Assert.Equal(-2, AiCheatHeuristics.TileValue(T(36), boundary));
            Assert.Equal(-2, AiCheatHeuristics.TileValue(T(72), boundary));
        }

        [Fact]
        public void SameTypeTilesNeverCountAsNeighbours()
        {
            // The V4 guard `it !== tile` only excludes the evaluated reference, and any
            // same-type tile sits at rank distance 0, which never matches distance 1/2.
            // A second physical copy must therefore neither add neighbour value nor be
            // wrongly excluded elsewhere.
            List<TileId> fives = H(16, 17);
            Assert.Equal(10, AiCheatHeuristics.TileValue(T(16), fives));
            // And the other copy of the same type is not excluded from any count.
            Assert.Equal(18, AiCheatHeuristics.TileValue(T(16), H(16, 17, 18)));
        }

        // ---------------------------------------------------------------------
        // Terminals, simples, honors, red five
        // ---------------------------------------------------------------------

        [Fact]
        public void TerminalsPayTwoAndSimplesPayNothing()
        {
            // 1m with a distance-1 neighbour: +5 bonus, -2 terminal penalty.
            Assert.Equal(3, AiCheatHeuristics.TileValue(T(0), H(0, 4)));
            // 9m with a pair bonus: +10 - 2.
            Assert.Equal(8, AiCheatHeuristics.TileValue(T(32), H(32, 33)));
            // Rank 9 only: 7m is not a terminal.
            Assert.Equal(0, AiCheatHeuristics.TileValue(T(24), H(24)));
            // 3m (simple) with a distance-1 neighbour: +5, no penalty.
            Assert.Equal(5, AiCheatHeuristics.TileValue(T(8), H(8, 12)));
        }

        [Fact]
        public void HonorsGetFourForDragonsAndOneForWinds()
        {
            Assert.Equal(4, AiCheatHeuristics.TileValue(T(124), H(124)));
            Assert.Equal(1, AiCheatHeuristics.TileValue(T(108), H(108)));
            // Honor pairs stack with the suit bonus.
            Assert.Equal(11, AiCheatHeuristics.TileValue(T(108), H(108, 109)));
            Assert.Equal(22, AiCheatHeuristics.TileValue(T(124), H(124, 125, 126)));
            // Honors have no rank-distance neighbours even within the same suit.
            Assert.Equal(1, AiCheatHeuristics.TileValue(T(108), H(108, 112)));
        }

        [Fact]
        public void RedFiveHasNoExtraWeightOverAnOrdinaryFive()
        {
            // V4 has no red-five adjustment in this heuristic: copy 3 scores exactly like
            // any other physical copy of the same type.
            List<TileId> hand = H(16, 17, 18, 19, 12, 13);
            Assert.Equal(AiCheatHeuristics.TileValue(T(18), hand), AiCheatHeuristics.TileValue(T(19), hand));
            // same=4 (+18) plus two distance-1 4m neighbours (+10); no far neighbour here.
            Assert.Equal(28, AiCheatHeuristics.TileValue(T(19), hand));
            // Red five in isolation: same simple-five score as copy 0.
            Assert.Equal(AiCheatHeuristics.TileValue(T(16), H()), AiCheatHeuristics.TileValue(T(19), H()));
        }

        [Fact]
        public void HandValueMixesAllComponentsLikeTheV4Formula()
        {
            // 1m in [1m, 2m, 3m]: same=1 (single), near=1 (2m) +5, far=1 (3m) +2, -2.
            Assert.Equal(5, AiCheatHeuristics.TileValue(T(0), H(0, 4, 8)));
            // 5m in [4m c0, 4m c1, 5m c0..c3, 6m c0, 6m c1]:
            // same=4 (+18), near=4 (+20), far=0 → 38.
            Assert.Equal(38, AiCheatHeuristics.TileValue(T(16), H(12, 13, 16, 17, 18, 19, 20, 21)));
            // 4m in the same hand: same=2 (+10), near=4 fives (+20), far=2 sixes (+4) → 34.
            Assert.Equal(34, AiCheatHeuristics.TileValue(T(12), H(12, 13, 16, 17, 18, 19, 20, 21)));
        }

        // ---------------------------------------------------------------------
        // Relative value and candidates outside the hand
        // ---------------------------------------------------------------------

        [Fact]
        public void RelativeValueIsExactlyTileValueInsideAndOutsideTheHand()
        {
            List<TileId> hand = H(16, 17, 18, 40, 108);
            for (int id = 0; id <= 135; id++)
            {
                Assert.Equal(
                    AiCheatHeuristics.TileValue(T(id), hand),
                    AiCheatHeuristics.RelativeValue(T(id), hand));
            }

            // A candidate absent from the list is scored as-is (1p outside: -2).
            Assert.Equal(-2, AiCheatHeuristics.RelativeValue(T(72), hand));
            // A candidate present in the list matches too.
            Assert.Equal(AiCheatHeuristics.TileValue(T(16), hand), AiCheatHeuristics.RelativeValue(T(16), hand));
        }

        // ---------------------------------------------------------------------
        // Sorting: both directions, ties, limits
        // ---------------------------------------------------------------------

        [Fact]
        public void LeastSortsAscendingAndMostDescendingByValue()
        {
            // Values in [1m, 9m, 1s, 2m, East, White]:
            //   1m +5-2=3, 9m -2, 1s -2, 2m +5, East +1, White +4.
            List<TileId> hand = H(0, 32, 36, 4, 108, 124);
            Assert.Equal(new[] { 32, 36, 108, 0, 124, 4 }, Ids(AiCheatHeuristics.LeastValuableHandTiles(hand, 6)));
            Assert.Equal(new[] { 4, 124, 0, 108, 32, 36 }, Ids(AiCheatHeuristics.MostValuableHandTiles(hand, 6)));
        }

        [Fact]
        public void TiesBreakByAscendingTileIdInBothDirections()
        {
            // 4m(+5) and 5m(+5) tie on value; equal values keep tile id ascending even
            // when sorting most-valuable-first.
            List<TileId> hand = H(16, 12);
            Assert.Equal(new[] { 12, 16 }, Ids(AiCheatHeuristics.LeastValuableHandTiles(hand, 2)));
            Assert.Equal(new[] { 12, 16 }, Ids(AiCheatHeuristics.MostValuableHandTiles(hand, 2)));
            // Three copies of 5m all score 18: id order in both directions.
            List<TileId> fives = H(18, 16, 17);
            Assert.Equal(new[] { 16, 17 }, Ids(AiCheatHeuristics.LeastValuableHandTiles(fives, 2)));
            Assert.Equal(new[] { 16, 17 }, Ids(AiCheatHeuristics.MostValuableHandTiles(fives, 2)));
            // Terminal tie (9m and 1s both -2) resolves by id: 32 before 36.
            List<TileId> terminals = H(36, 32);
            Assert.Equal(new[] { 32, 36 }, Ids(AiCheatHeuristics.LeastValuableHandTiles(terminals, 2)));
            Assert.Equal(new[] { 32, 36 }, Ids(AiCheatHeuristics.MostValuableHandTiles(terminals, 2)));
        }

        [Fact]
        public void LimitZeroReturnsEmptyAndOversizedReturnsEverything()
        {
            List<TileId> hand = H(16, 0, 108);
            Assert.Empty(AiCheatHeuristics.LeastValuableHandTiles(hand, 0));
            Assert.Empty(AiCheatHeuristics.MostValuableHandTiles(hand, 0));
            // 1m=-2, 5m=0, East=+1.
            Assert.Equal(new[] { 0, 16, 108 }, Ids(AiCheatHeuristics.LeastValuableHandTiles(hand, 3)));
            // Larger than the hand: everything, no error (Kotlin take(n > size)).
            Assert.Equal(new[] { 0, 16, 108 }, Ids(AiCheatHeuristics.LeastValuableHandTiles(hand, 4)));
            Assert.Equal(new[] { 108, 16, 0 }, Ids(AiCheatHeuristics.MostValuableHandTiles(hand, 64)));
        }

        [Fact]
        public void NegativeLimitThrowsLikeTheV4Take()
        {
            List<TileId> hand = H(16, 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => AiCheatHeuristics.LeastValuableHandTiles(hand, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => AiCheatHeuristics.MostValuableHandTiles(hand, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => AiCheatHeuristics.LeastValuableHandTiles(hand, -7));
            // A null list must be rejected before the limit is looked at.
            Assert.Throws<ArgumentNullException>(() => AiCheatHeuristics.LeastValuableHandTiles(null!, -1));
        }

        [Fact]
        public void NoHiddenCandidateLimitOrHandSizeCapIsApplied()
        {
            // A 20-entry list of one physical tile: limit 15 returns 15 — no 14-tile cap,
            // no candidate-limit-64 clamp, duplicates accepted like in V4.
            var repeats = new List<TileId>(20);
            for (int i = 0; i < 20; i++)
            {
                repeats.Add(T(16));
            }

            Assert.Equal(15, AiCheatHeuristics.LeastValuableHandTiles(repeats, 15).Count);
            Assert.Equal(15, AiCheatHeuristics.MostValuableHandTiles(repeats, 15).Count);
            Assert.Equal(20, AiCheatHeuristics.LeastValuableHandTiles(repeats, 64).Count);
            // A limit beyond the list length is not clamped to 64 or the count — Kotlin
            // take simply returns everything (20 entries here).
            Assert.Equal(20, AiCheatHeuristics.MostValuableHandTiles(repeats, 100).Count);
        }

        [Fact]
        public void PhysicalIdentitySurvivesSortingAndValuesAreOrderingOnly()
        {
            // Output entries are the same physical ids that went in — copies are never
            // merged into a canonical representative. Values: red 5m = 0 + 2×5 = 10,
            // each 4m = 10 + 5 = 15, so the five sorts first.
            List<TileId> hand = H(19, 12, 13);
            IReadOnlyList<TileId> least = AiCheatHeuristics.LeastValuableHandTiles(hand, 3);
            Assert.Equal(new[] { 19, 12, 13 }, Ids(least));
            Assert.Equal(19, least[0].Value);
            Assert.Equal(3, least[0].CopyIndex);
            Assert.True(least[0].IsRedFive);
        }

        // ---------------------------------------------------------------------
        // Input immutability and output isolation
        // ---------------------------------------------------------------------

        [Fact]
        public void SortingNeverMutatesTheInputList()
        {
            var hand = H(124, 16, 0, 108, 32);
            int[] snapshot = Ids(hand.ToArray());

            AiCheatHeuristics.LeastValuableHandTiles(hand, 5);
            AiCheatHeuristics.MostValuableHandTiles(hand, 5);
            AiCheatHeuristics.LeastValuableHandTiles(hand, 0);
            AiCheatHeuristics.TileValue(T(16), hand);
            AiCheatHeuristics.RelativeValue(T(124), hand);

            Assert.Equal(snapshot, Ids(hand.ToArray()));
        }

        [Fact]
        public void OutputIsADefensiveCopyIsolatedFromTheInput()
        {
            var hand = H(16, 0, 108);
            IReadOnlyList<TileId> least = AiCheatHeuristics.LeastValuableHandTiles(hand, 3);

            // Not the same collection instance as the input.
            Assert.NotSame(hand, least);
            // Mutating the input afterwards cannot change the frozen output.
            hand[0] = T(124);
            Assert.Equal(new[] { 0, 16, 108 }, Ids(least));
            // The read-only wrapper refuses writes.
            Assert.Throws<NotSupportedException>(() => ((ICollection<TileId>)least).Add(T(0)));
            Assert.Throws<NotSupportedException>(() => ((ICollection<TileId>)least).Clear());
        }

        [Fact]
        public void NullListIsRejectedAtEveryEntryPoint()
        {
            Assert.Throws<ArgumentNullException>(() => AiCheatHeuristics.TileValue(T(0), null!));
            Assert.Throws<ArgumentNullException>(() => AiCheatHeuristics.LeastValuableHandTiles(null!, 1));
            Assert.Throws<ArgumentNullException>(() => AiCheatHeuristics.MostValuableHandTiles(null!, 1));
            Assert.Throws<ArgumentNullException>(() => AiCheatHeuristics.RelativeValue(T(0), null!));
        }

        private static int[] Ids(IReadOnlyList<TileId> tiles)
        {
            var ids = new int[tiles.Count];
            for (int i = 0; i < tiles.Count; i++)
            {
                ids[i] = tiles[i].Value;
            }

            return ids;
        }
    }
}
