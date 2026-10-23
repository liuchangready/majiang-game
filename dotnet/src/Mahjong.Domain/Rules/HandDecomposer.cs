using System;
using System.Collections.Generic;
using System.Linq;

namespace Mahjong.Domain.Rules
{
    public enum DecompPattern
    {
        Regular,
        Chiitoitsu,
        Kokushi
    }

    /// <summary>One sequence or triplet in a decomposition.</summary>
    public sealed class MentsuGroup
    {
        public MentsuGroup(IEnumerable<int> typeIndices, bool isSequence)
        {
            if (typeIndices == null)
            {
                throw new ArgumentNullException(nameof(typeIndices));
            }

            int[] materialized = typeIndices.ToArray();
            if (materialized.Length != 3)
            {
                throw new ArgumentException("A mentsu must contain three type indices.", nameof(typeIndices));
            }

            for (int i = 0; i < materialized.Length; i++)
            {
                if (materialized[i] < 0 || materialized[i] >= TileId.TypeCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(typeIndices), materialized[i], "typeIndex must be 0..33.");
                }
            }

            if (isSequence)
            {
                if (materialized[0] >= 27 || materialized[1] != materialized[0] + 1 ||
                    materialized[2] != materialized[1] + 1 || materialized[0] / 9 != materialized[2] / 9)
                {
                    throw new ArgumentException("A sequence must ascend by one inside one number suit.", nameof(typeIndices));
                }
            }
            else if (materialized[1] != materialized[0] || materialized[2] != materialized[0])
            {
                throw new ArgumentException("A triplet must repeat one type index.", nameof(typeIndices));
            }

            TypeIndices = Array.AsReadOnly(materialized);
            IsSequence = isSequence;
        }

        public IReadOnlyList<int> TypeIndices { get; }

        public bool IsSequence { get; }

        public bool IsTriple => !IsSequence;
    }

    /// <summary>
    /// One complete interpretation of the concealed portion of a hand. V4 enumerates all
    /// interpretations; this type is immutable so evaluators can safely retain one.
    /// </summary>
    public sealed class Decomposition
    {
        public Decomposition(IEnumerable<MentsuGroup> mentsu, int jantai, DecompPattern pattern)
        {
            if (mentsu == null)
            {
                throw new ArgumentNullException(nameof(mentsu));
            }

            _mentsu = mentsu.ToArray();
            if (jantai < -1 || jantai >= TileId.TypeCount)
            {
                throw new ArgumentOutOfRangeException(nameof(jantai), jantai, "jantai must be -1 or 0..33.");
            }

            Jantai = jantai;
            Pattern = pattern;
        }

        private readonly MentsuGroup[] _mentsu;

        public IReadOnlyList<MentsuGroup> Mentsu => Array.AsReadOnly(_mentsu);

        public int Jantai { get; }

        public DecompPattern Pattern { get; }
    }

    /// <summary>
    /// Port of V4 <c>HandDecomposer</c>. Enumeration order is intentionally identical:
    /// regular decompositions first (jantai ascending, triplets before sequences), then
    /// chiitoitsu, then kokushi. <c>WinningScoreEvaluator</c> relies on that order for ties.
    /// </summary>
    public static class HandDecomposer
    {
        public static IReadOnlyList<Decomposition> Decompose(IEnumerable<TileId> closedTiles)
        {
            if (closedTiles == null)
            {
                throw new ArgumentNullException(nameof(closedTiles));
            }

            TileId[] tiles = closedTiles.ToArray();
            var counts = new int[TileId.TypeCount];
            foreach (TileId tile in tiles)
            {
                if (++counts[tile.TypeIndex] > 4)
                {
                    throw new ArgumentException("A hand cannot contain more than four tiles of one type.", nameof(closedTiles));
                }
            }

            var results = new List<Decomposition>();
            FindAllRegularDecompositions(counts, counts.Sum(), results);
            DecomposeChiitoitsu(counts, results);
            DecomposeKokushi(counts, results);
            return results.AsReadOnly();
        }

        public static bool CanWin(IEnumerable<TileId> closedTiles)
        {
            if (closedTiles == null)
            {
                throw new ArgumentNullException(nameof(closedTiles));
            }

            var counts = new int[TileId.TypeCount];
            foreach (TileId tile in closedTiles)
            {
                counts[tile.TypeIndex]++;
            }

            return CanWinFromCounts(counts);
        }

        public static IReadOnlyCollection<int> CalculateWaits(IEnumerable<TileId> closedTiles)
        {
            if (closedTiles == null)
            {
                throw new ArgumentNullException(nameof(closedTiles));
            }

            var counts = new int[TileId.TypeCount];
            foreach (TileId tile in closedTiles)
            {
                counts[tile.TypeIndex]++;
            }

            var waits = new SortedSet<int>();
            for (int candidate = 0; candidate < TileId.TypeCount; candidate++)
            {
                if (counts[candidate] >= 4)
                {
                    continue;
                }

                int[] testCounts = (int[])counts.Clone();
                testCounts[candidate]++;
                if (CanWinFromCounts(testCounts))
                {
                    waits.Add(candidate);
                }
            }

            return waits.ToArray();
        }

        public static bool CanWinFromCounts(int[] counts)
        {
            if (counts == null)
            {
                throw new ArgumentNullException(nameof(counts));
            }

            if (counts.Length != TileId.TypeCount)
            {
                throw new ArgumentException("counts must have 34 entries.", nameof(counts));
            }

            int total = counts.Sum();
            if (total != 14 && total != 2 && total != 5 && total != 8 && total != 11)
            {
                if (total % 3 != 2)
                {
                    return false;
                }
            }

            int neededMentsu = (total - 2) / 3;
            if ((total - 2) % 3 == 0 && neededMentsu >= 0 && neededMentsu <= 4)
            {
                for (int jantai = 0; jantai < TileId.TypeCount; jantai++)
                {
                    if (counts[jantai] < 2)
                    {
                        continue;
                    }

                    int[] remaining = (int[])counts.Clone();
                    remaining[jantai] -= 2;
                    if (CanExtractAllMentsu(remaining, 0, neededMentsu))
                    {
                        return true;
                    }
                }
            }

            if (total == 14)
            {
                int pairCount = 0;
                bool valid = true;
                foreach (int count in counts)
                {
                    if (count == 0)
                    {
                        continue;
                    }

                    if (count == 2)
                    {
                        pairCount++;
                    }
                    else if (count == 4)
                    {
                        pairCount += 2;
                    }
                    else
                    {
                        valid = false;
                        break;
                    }
                }

                if (valid && pairCount == 7)
                {
                    return true;
                }
            }

            if (total == 14 && CheckKokushi(counts))
            {
                return true;
            }

            return false;
        }

        private static void FindAllRegularDecompositions(int[] originalCounts, int totalTiles, List<Decomposition> results)
        {
            int neededMentsu = (totalTiles - 2) / 3;
            if ((totalTiles - 2) % 3 != 0 || neededMentsu < 0 || neededMentsu > 4)
            {
                return;
            }

            for (int jantai = 0; jantai < TileId.TypeCount; jantai++)
            {
                if (originalCounts[jantai] < 2)
                {
                    continue;
                }

                int[] remaining = (int[])originalCounts.Clone();
                remaining[jantai] -= 2;

                var allMentsuSets = new List<List<MentsuGroup>>();
                FindAllMentsuCombinations(remaining, 0, neededMentsu, new List<MentsuGroup>(), allMentsuSets);
                foreach (List<MentsuGroup> mentsuSet in allMentsuSets)
                {
                    results.Add(new Decomposition(mentsuSet, jantai, DecompPattern.Regular));
                }
            }
        }

        private static void FindAllMentsuCombinations(
            int[] counts,
            int startIndex,
            int remaining,
            List<MentsuGroup> current,
            List<List<MentsuGroup>> results)
        {
            if (remaining == 0)
            {
                if (counts.All(c => c == 0))
                {
                    results.Add(current.ToList());
                }

                return;
            }

            int idx = startIndex;
            while (idx < TileId.TypeCount && counts[idx] == 0)
            {
                idx++;
            }

            if (idx >= TileId.TypeCount)
            {
                return;
            }

            if (counts[idx] >= 3)
            {
                counts[idx] -= 3;
                current.Add(new MentsuGroup(new[] { idx, idx, idx }, isSequence: false));
                FindAllMentsuCombinations(counts, idx, remaining - 1, current, results);
                current.RemoveAt(current.Count - 1);
                counts[idx] += 3;
            }

            if (idx < 27)
            {
                int suitBase = (idx / 9) * 9;
                int rank = idx - suitBase;
                if (rank <= 6)
                {
                    int i1 = idx;
                    int i2 = idx + 1;
                    int i3 = idx + 2;
                    if (i2 < suitBase + 9 && i3 < suitBase + 9 &&
                        counts[i1] >= 1 && counts[i2] >= 1 && counts[i3] >= 1)
                    {
                        counts[i1]--;
                        counts[i2]--;
                        counts[i3]--;
                        current.Add(new MentsuGroup(new[] { i1, i2, i3 }, isSequence: true));
                        FindAllMentsuCombinations(counts, idx, remaining - 1, current, results);
                        current.RemoveAt(current.Count - 1);
                        counts[i1]++;
                        counts[i2]++;
                        counts[i3]++;
                    }
                }
            }
        }

        private static void DecomposeChiitoitsu(int[] counts, List<Decomposition> results)
        {
            if (counts.Sum() != 14)
            {
                return;
            }

            var pairs = new List<int>();
            for (int i = 0; i < TileId.TypeCount; i++)
            {
                if (counts[i] == 2)
                {
                    pairs.Add(i);
                }
                else if (counts[i] == 4)
                {
                    pairs.Add(i);
                    pairs.Add(i);
                }
                else if (counts[i] != 0)
                {
                    return;
                }
            }

            if (pairs.Count == 7)
            {
                results.Add(new Decomposition(Array.Empty<MentsuGroup>(), -1, DecompPattern.Chiitoitsu));
            }
        }

        private static void DecomposeKokushi(int[] counts, List<Decomposition> results)
        {
            if (counts.Sum() != 14)
            {
                return;
            }

            int[] kokushiIndices = { 0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33 };
            foreach (int idx in kokushiIndices)
            {
                if (counts[idx] < 1)
                {
                    return;
                }
            }

            for (int i = 0; i < TileId.TypeCount; i++)
            {
                if (!kokushiIndices.Contains(i) && counts[i] > 0)
                {
                    return;
                }
            }

            foreach (int idx in kokushiIndices)
            {
                if (counts[idx] == 2)
                {
                    results.Add(new Decomposition(Array.Empty<MentsuGroup>(), idx, DecompPattern.Kokushi));
                }
            }
        }

        private static bool CanExtractAllMentsu(int[] counts, int startIndex, int remaining)
        {
            if (remaining == 0)
            {
                return counts.All(c => c == 0);
            }

            int idx = startIndex;
            while (idx < TileId.TypeCount && counts[idx] == 0)
            {
                idx++;
            }

            if (idx >= TileId.TypeCount)
            {
                return false;
            }

            if (counts[idx] >= 3)
            {
                counts[idx] -= 3;
                bool found = CanExtractAllMentsu(counts, idx, remaining - 1);
                counts[idx] += 3;
                if (found)
                {
                    return true;
                }
            }

            if (idx < 27)
            {
                int suitBase = (idx / 9) * 9;
                int rank = idx - suitBase;
                if (rank <= 6)
                {
                    int i1 = idx;
                    int i2 = idx + 1;
                    int i3 = idx + 2;
                    if (i2 < suitBase + 9 && i3 < suitBase + 9 &&
                        counts[i1] >= 1 && counts[i2] >= 1 && counts[i3] >= 1)
                    {
                        counts[i1]--;
                        counts[i2]--;
                        counts[i3]--;
                        bool found = CanExtractAllMentsu(counts, idx, remaining - 1);
                        counts[i1]++;
                        counts[i2]++;
                        counts[i3]++;
                        if (found)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool CheckKokushi(int[] counts)
        {
            int[] kokushiIndices = { 0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33 };
            bool hasPair = false;
            for (int i = 0; i < TileId.TypeCount; i++)
            {
                if (kokushiIndices.Contains(i))
                {
                    if (counts[i] == 0)
                    {
                        return false;
                    }

                    if (counts[i] == 1)
                    {
                        continue;
                    }

                    if (counts[i] == 2 && !hasPair)
                    {
                        hasPair = true;
                        continue;
                    }

                    return false;
                }

                if (counts[i] > 0)
                {
                    return false;
                }
            }

            return hasPair;
        }
    }
}
