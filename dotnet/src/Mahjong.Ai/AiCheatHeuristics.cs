using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Mahjong.Domain;

namespace Mahjong.Ai
{
    /// <summary>
    /// Stateless visible-tile sorting heuristics for AI cheat candidates (ENG-021 port of
    /// V4 <c>engine/src/main/kotlin/com/majiang/ai/cheat/AiCheatHeuristics.kt</c>).
    ///
    /// Every input is a list of physical <see cref="TileId"/> values that the viewing seat
    /// is already authorized to see (closed hand plus drawn tile, or an authoritative
    /// selectable list). The type only ranks and estimates; it never reads
    /// <c>GameState</c>, the 136-tile position map, the wall order, another seat's closed
    /// hand, or any authority object, and it changes no rule or physical outcome. The
    /// returned integer scores exist only to order candidates: they must never enter a
    /// <c>CommandIntent</c> canonical hash or an authoritative charge/ledger fact.
    ///
    /// Semantics are ported from the V4 source behaviour, not its comments:
    /// <list type="bullet">
    /// <item><description><see cref="TileValue"/> counts every same-type tile in the
    /// input list, including the evaluated tile itself when it is present, and awards
    /// +18 for three or more, +10 for exactly two. Number tiles then add 5 per same-suit
    /// rank-distance-1 neighbour and 2 per rank-distance-2 neighbour, and subtract 2 for
    /// a 1/9 terminal. Honors add 4 for dragons and 1 for winds. There is deliberately no
    /// self-wind, round-wind, dora or red-five weighting — the V4 heuristic has none.</description></item>
    /// <item><description>The V4 neighbour predicates additionally exclude the evaluated
    /// tile by reference (<c>it !== tile</c>). That guard is provably inert: a tile at
    /// rank distance 1 or 2 necessarily has a different rank, so the evaluated tile itself
    /// (distance 0) and any other physical copy of the same type can never satisfy the
    /// distance predicate with or without the guard. This port therefore uses the pure
    /// value predicate and never excludes a second physical copy; the Kotlin/C#
    /// differential probe over all 136 candidates against many hands confirms zero
    /// divergence.</description></item>
    /// <item><description>The least/most selections sort by value (ascending for least,
    /// descending for most), break ties by ascending <see cref="TileId"/>, and keep the
    /// input order of fully equal entries (LINQ <c>OrderBy</c>/<c>OrderByDescending</c>
    /// are stable, matching Kotlin's stable <c>sortedWith</c>). Distinct physical tiles
    /// are totally ordered by (value, tile id), so the only fully-equal pairs are
    /// duplicates of the same physical tile, which are indistinguishable in the output.</description></item>
    /// <item><description><c>limit</c> follows Kotlin <c>take</c>: a negative limit
    /// throws (Kotlin <c>IllegalArgumentException</c> mapped to the .NET convention
    /// <see cref="ArgumentOutOfRangeException"/>), zero returns an empty list, and a limit
    /// larger than the input returns every tile. There is no 14-tile cap and no
    /// candidate-limit-64 clamp: the V4 functions accept any list, duplicates included,
    /// and this port keeps that exact input range.</description></item>
    /// <item><description>A null list is rejected at the entry point with
    /// <see cref="ArgumentNullException"/>, which is the C# convention mapped from the
    /// Kotlin compiler's intrinsic null check on the non-null V4 parameters
    /// (<c>NullPointerException</c>). Lists are never mutated; results are fresh
    /// <see cref="ReadOnlyCollection{TileId}"/> copies and no writable collection ever
    /// escapes.</description></item>
    /// </list>
    /// </summary>
    public static class AiCheatHeuristics
    {
        /// <summary>
        /// Retention value of one tile inside the given visible list (higher is worth
        /// keeping). <paramref name="tile"/> does not have to be a member of
        /// <paramref name="hand"/>.
        /// </summary>
        public static int TileValue(TileId tile, IReadOnlyList<TileId> hand)
        {
            if (hand == null)
            {
                throw new ArgumentNullException(nameof(hand));
            }

            var sameTypeCount = 0;
            for (int i = 0; i < hand.Count; i++)
            {
                if (hand[i].SameType(tile))
                {
                    sameTypeCount++;
                }
            }

            var value = sameTypeCount >= 3 ? 18 : sameTypeCount == 2 ? 10 : 0;
            if (tile.IsNumberTile)
            {
                var near = 0;
                var far = 0;
                for (int i = 0; i < hand.Count; i++)
                {
                    TileId other = hand[i];
                    if (other.Suit != tile.Suit)
                    {
                        continue;
                    }

                    int distance = Math.Abs(other.Rank - tile.Rank);
                    if (distance == 1)
                    {
                        near++;
                    }
                    else if (distance == 2)
                    {
                        far++;
                    }
                }

                value += (near * 5) + (far * 2);
                if (tile.Rank == 1 || tile.Rank == 9)
                {
                    value -= 2;
                }
            }
            else
            {
                value += tile.Suit == TileSuit.Dragon ? 4 : 1;
            }

            return value;
        }

        /// <summary>
        /// The <paramref name="limit"/> lowest-value tiles of the visible list, in
        /// (value, tile id) order — the V4 shape used to pick a beam-swap give-away tile.
        /// Never mutates the input; returns a fresh read-only copy.
        /// </summary>
        public static IReadOnlyList<TileId> LeastValuableHandTiles(IReadOnlyList<TileId> hand, int limit)
        {
            return SortedByValue(hand, limit, descending: false);
        }

        /// <summary>
        /// The <paramref name="limit"/> highest-value tiles of the visible list, in
        /// descending value then ascending tile id order — the V4 shape used to pick
        /// tile-mark targets. Never mutates the input; returns a fresh read-only copy.
        /// </summary>
        public static IReadOnlyList<TileId> MostValuableHandTiles(IReadOnlyList<TileId> hand, int limit)
        {
            return SortedByValue(hand, limit, descending: true);
        }

        /// <summary>
        /// Relative value of one candidate tile in the context of the visible list.
        /// V4 defines this as exactly <c>tileValue</c>; the candidate may be absent from
        /// <paramref name="hand"/>.
        /// </summary>
        public static int RelativeValue(TileId candidate, IReadOnlyList<TileId> hand)
        {
            return TileValue(candidate, hand);
        }

        private static IReadOnlyList<TileId> SortedByValue(IReadOnlyList<TileId> hand, int limit, bool descending)
        {
            if (hand == null)
            {
                throw new ArgumentNullException(nameof(hand));
            }

            if (limit < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(limit),
                    limit,
                    "Limit must be non-negative; the V4 Kotlin take(negative) throws.");
            }

            // OrderBy/OrderByDescending evaluate the key selector once per element and are
            // documented stable, which is exactly Kotlin sortedWith + thenBy(tileId).
            IOrderedEnumerable<TileId> ordered = descending
                ? hand.OrderByDescending(tile => TileValue(tile, hand)).ThenBy(tile => tile.Value)
                : hand.OrderBy(tile => TileValue(tile, hand)).ThenBy(tile => tile.Value);

            var result = ordered.Take(limit).ToArray();
            return Array.AsReadOnly(result);
        }
    }
}
