using System;
using System.Collections.Generic;
using Mahjong.Domain;

namespace Mahjong.Ai
{
    /// <summary>
    /// Pure visible-tile shanten calculator over a seat's own known hand tiles.
    /// Computes standard regular hand shanten, plus Chiitoitsu and Kokushi when a full
    /// closed hand (13 or 14 tiles) is evaluated. Never reads hidden state or walls.
    /// </summary>
    public static class AiShantenCalculator
    {
        /// <summary>
        /// Returns shanten of <paramref name="closedTiles"/> (-1 = complete/winning hand,
        /// 0 = tenpai, 1 = 1-shanten, ...). Handles open-meld reduced closed hands (e.g.
        /// 1, 2, 4, 5, 7, 8, 10, 11, 13, 14 tiles).
        /// </summary>
        public static int CalculateShanten(IReadOnlyList<TileId> closedTiles)
        {
            if (closedTiles == null)
            {
                throw new ArgumentNullException(nameof(closedTiles));
            }

            if (closedTiles.Count == 0)
            {
                return 8;
            }

            var counts = new int[TileId.TypeCount];
            for (int i = 0; i < closedTiles.Count; i++)
            {
                counts[closedTiles[i].TypeIndex]++;
            }

            int targetMentsu = closedTiles.Count / 3;
            if (targetMentsu > 4)
            {
                targetMentsu = 4;
            }

            int best = CalculateRegularShanten(counts, targetMentsu);
            if (targetMentsu == 4 && closedTiles.Count >= 13)
            {
                int chiitoi = CalculateChiitoitsuShanten(counts);
                if (chiitoi < best)
                {
                    best = chiitoi;
                }

                int kokushi = CalculateKokushiShanten(counts);
                if (kokushi < best)
                {
                    best = kokushi;
                }
            }

            return best;
        }

        /// <summary>
        /// Evaluates effective hand progress when holding <paramref name="handTiles"/>.
        /// For 3k+2 hands (e.g. 14 tiles before discarding), returns -1 if already winning
        /// and otherwise the minimum shanten achievable after discarding one visible tile.
        /// </summary>
        public static int CalculateBestPostDiscardShanten(IReadOnlyList<TileId> handTiles)
        {
            if (handTiles == null)
            {
                throw new ArgumentNullException(nameof(handTiles));
            }

            if (handTiles.Count == 0)
            {
                return 8;
            }

            if (handTiles.Count % 3 != 2)
            {
                return CalculateShanten(handTiles);
            }

            var counts = new int[TileId.TypeCount];
            for (int i = 0; i < handTiles.Count; i++)
            {
                counts[handTiles[i].TypeIndex]++;
            }

            int targetMentsu = handTiles.Count / 3;
            if (targetMentsu > 4)
            {
                targetMentsu = 4;
            }

            int immediate = EvaluateFromCounts(counts, handTiles.Count, targetMentsu);
            if (immediate < 0)
            {
                return -1;
            }

            int bestAfterDiscard = 8;
            int afterTarget = (handTiles.Count - 1) / 3;
            if (afterTarget > 4)
            {
                afterTarget = 4;
            }

            for (int typeIndex = 0; typeIndex < TileId.TypeCount; typeIndex++)
            {
                if (counts[typeIndex] <= 0)
                {
                    continue;
                }

                counts[typeIndex]--;
                int shanten = EvaluateFromCounts(counts, handTiles.Count - 1, afterTarget);
                counts[typeIndex]++;

                if (shanten < bestAfterDiscard)
                {
                    bestAfterDiscard = shanten;
                    if (bestAfterDiscard <= 0)
                    {
                        break;
                    }
                }
            }

            return bestAfterDiscard;
        }

        internal static int EvaluateFromCounts(int[] counts, int tileCount, int targetMentsu)
        {
            int best = CalculateRegularShanten(counts, targetMentsu);
            if (targetMentsu == 4 && tileCount >= 13)
            {
                int chiitoi = CalculateChiitoitsuShanten(counts);
                if (chiitoi < best)
                {
                    best = chiitoi;
                }

                int kokushi = CalculateKokushiShanten(counts);
                if (kokushi < best)
                {
                    best = kokushi;
                }
            }

            return best;
        }

        private static int CalculateChiitoitsuShanten(int[] counts)
        {
            int pairs = 0;
            int distinct = 0;
            for (int i = 0; i < TileId.TypeCount; i++)
            {
                if (counts[i] >= 1)
                {
                    distinct++;
                    if (counts[i] >= 2)
                    {
                        pairs++;
                    }
                }
            }

            int missingTypes = distinct < 7 ? 7 - distinct : 0;
            return 6 - pairs + missingTypes;
        }

        private static int CalculateKokushiShanten(int[] counts)
        {
            int unique = 0;
            bool hasPair = false;
            for (int i = 0; i < TileId.TypeCount; i++)
            {
                if (!IsTerminalOrHonor(i) || counts[i] <= 0)
                {
                    continue;
                }

                unique++;
                if (counts[i] >= 2)
                {
                    hasPair = true;
                }
            }

            return 13 - unique - (hasPair ? 1 : 0);
        }

        private static bool IsTerminalOrHonor(int typeIndex)
        {
            if (typeIndex >= 27)
            {
                return true;
            }

            int rank = typeIndex % 9;
            return rank == 0 || rank == 8;
        }

        private static int CalculateRegularShanten(int[] counts, int targetMentsu)
        {
            var work = (int[])counts.Clone();
            int best = 2 * targetMentsu;

            // Try without pair
            Search(work, 0, targetMentsu, mentsu: 0, taatsu: 0, hasPair: 0, ref best);

            // Try each possible pair
            for (int i = 0; i < TileId.TypeCount; i++)
            {
                if (work[i] >= 2)
                {
                    work[i] -= 2;
                    Search(work, 0, targetMentsu, mentsu: 0, taatsu: 0, hasPair: 1, ref best);
                    work[i] += 2;
                }
            }

            return best;
        }

        private static void Search(
            int[] counts,
            int index,
            int targetMentsu,
            int mentsu,
            int taatsu,
            int hasPair,
            ref int best)
        {
            while (index < TileId.TypeCount && counts[index] == 0)
            {
                index++;
            }

            int effectiveTaatsu = mentsu + taatsu > targetMentsu ? targetMentsu - mentsu : taatsu;
            int current = (2 * targetMentsu) - (2 * mentsu) - effectiveTaatsu - hasPair;
            if (current < best)
            {
                best = current;
            }

            if (index >= TileId.TypeCount || mentsu == targetMentsu)
            {
                return;
            }

            // 1. Triplet
            if (counts[index] >= 3 && mentsu < targetMentsu)
            {
                counts[index] -= 3;
                Search(counts, index, targetMentsu, mentsu + 1, taatsu, hasPair, ref best);
                counts[index] += 3;
            }

            // 2. Sequence (only in number suits 0..26, rank <= 6)
            if (index < 27 && (index % 9) <= 6 && mentsu < targetMentsu &&
                counts[index] >= 1 && counts[index + 1] >= 1 && counts[index + 2] >= 1)
            {
                counts[index]--;
                counts[index + 1]--;
                counts[index + 2]--;
                Search(counts, index, targetMentsu, mentsu + 1, taatsu, hasPair, ref best);
                counts[index]++;
                counts[index + 1]++;
                counts[index + 2]++;
            }

            // 3. Taatsu branches (only useful when mentsu + taatsu < targetMentsu)
            if (mentsu + taatsu < targetMentsu)
            {
                // 3a. Pair taatsu
                if (counts[index] >= 2)
                {
                    counts[index] -= 2;
                    Search(counts, index, targetMentsu, mentsu, taatsu + 1, hasPair, ref best);
                    counts[index] += 2;
                }

                // 3b. Adjacent sequence wait (ryanmen/penchan)
                if (index < 27 && (index % 9) <= 7 &&
                    counts[index] >= 1 && counts[index + 1] >= 1)
                {
                    counts[index]--;
                    counts[index + 1]--;
                    Search(counts, index, targetMentsu, mentsu, taatsu + 1, hasPair, ref best);
                    counts[index]++;
                    counts[index + 1]++;
                }

                // 3c. Closed wait (kanchan)
                if (index < 27 && (index % 9) <= 6 &&
                    counts[index] >= 1 && counts[index + 2] >= 1)
                {
                    counts[index]--;
                    counts[index + 2]--;
                    Search(counts, index, targetMentsu, mentsu, taatsu + 1, hasPair, ref best);
                    counts[index]++;
                    counts[index + 2]++;
                }
            }

            // 4. Advance without consuming remaining copies of counts[index]
            Search(counts, index + 1, targetMentsu, mentsu, taatsu, hasPair, ref best);
        }
    }
}
