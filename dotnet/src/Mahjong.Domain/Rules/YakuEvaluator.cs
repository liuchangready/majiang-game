using System;
using System.Collections.Generic;
using System.Linq;

namespace Mahjong.Domain.Rules
{
    public sealed class YakuResult
    {
        public YakuResult(IEnumerable<YakuHit> yakuList, int totalHan, bool isYakuman, int yakumanCount)
        {
            if (yakuList == null)
            {
                throw new ArgumentNullException(nameof(yakuList));
            }

            YakuList = Array.AsReadOnly(yakuList.ToArray());
            TotalHan = totalHan;
            IsYakuman = isYakuman;
            YakumanCount = yakumanCount;
        }

        public IReadOnlyList<YakuHit> YakuList { get; }

        public int TotalHan { get; }

        public bool IsYakuman { get; }

        public int YakumanCount { get; }

        public static YakuResult Empty { get; } = new YakuResult(Array.Empty<YakuHit>(), 0, false, 0);
    }

    /// <summary>
    /// Port of V4 <c>YakuEvaluator</c>, including its exact yaku ordering and edge-case
    /// interpretation (for example the closed-kan contribution to concealed triplets).
    /// </summary>
    public static class YakuEvaluator
    {
        public static YakuResult Evaluate(PlayerHand hand, WinContext context)
        {
            if (hand == null)
            {
                throw new ArgumentNullException(nameof(hand));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            IReadOnlyList<Decomposition> decompositions = HandDecomposer.Decompose(hand.AllClosedTiles);
            YakuResult best = YakuResult.Empty;
            foreach (Decomposition decomposition in decompositions)
            {
                YakuResult result = EvaluateForDecomposition(decomposition, hand, context);
                if (result.IsYakuman)
                {
                    if (!best.IsYakuman || result.YakumanCount > best.YakumanCount)
                    {
                        best = result;
                    }
                }
                else if (!best.IsYakuman && result.TotalHan > best.TotalHan)
                {
                    best = result;
                }
            }

            return best;
        }

        public static YakuResult EvaluateForDecomposition(
            Decomposition decomposition,
            PlayerHand hand,
            WinContext context)
        {
            if (decomposition == null)
            {
                throw new ArgumentNullException(nameof(decomposition));
            }

            if (hand == null)
            {
                throw new ArgumentNullException(nameof(hand));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            TileId[] allTiles = hand.AllClosedTiles.Concat(hand.Melds.SelectMany(m => m.Tiles)).ToArray();

            var yakuman = new List<Yaku>();
            if (CheckKokushi(decomposition))
            {
                yakuman.Add(Yaku.Kokushi);
            }

            if (CheckSuuankou(decomposition, hand, context))
            {
                yakuman.Add(Yaku.Suuankou);
            }

            if (CheckDaisangen(decomposition, hand))
            {
                yakuman.Add(Yaku.Daisangen);
            }

            if (CheckShousuushii(decomposition, hand))
            {
                yakuman.Add(Yaku.Shousuushii);
            }

            if (CheckDaisuushii(decomposition, hand))
            {
                yakuman.Add(Yaku.Daisuushii);
            }

            if (allTiles.All(t => t.IsHonor))
            {
                yakuman.Add(Yaku.Tsuuiisou);
            }

            if (allTiles.All(t => TileFacts.IsGreen(t.TypeIndex)))
            {
                yakuman.Add(Yaku.Ryuuiisou);
            }

            if (allTiles.All(t => t.IsTerminal))
            {
                yakuman.Add(Yaku.Chinroutou);
            }

            if (CheckChurenpoutou(decomposition, hand, context))
            {
                yakuman.Add(Yaku.Churenpoutou);
            }

            if (hand.Melds.Count(m => m.IsKan) == 4)
            {
                yakuman.Add(Yaku.Suukantsu);
            }

            if (context.IsTenhou)
            {
                yakuman.Add(Yaku.Tenhou);
            }

            if (context.IsChiihou)
            {
                yakuman.Add(Yaku.Chiihou);
            }

            if (yakuman.Count > 0)
            {
                return new YakuResult(yakuman.Select(y => y.Hit(0)), 0, true, yakuman.Count);
            }

            var normal = new List<YakuHit>();

            void AddYaku(Yaku yaku)
            {
                int han = context.IsClosed ? yaku.HanClosed : yaku.HanOpen;
                if (han > 0)
                {
                    normal.Add(yaku.Hit(han));
                }
            }

            if (context.IsRiichi && !context.IsDoubleRiichi)
            {
                AddYaku(Yaku.Riichi);
            }

            if (context.IsDoubleRiichi)
            {
                AddYaku(Yaku.DoubleRiichi);
            }

            if (context.IsIppatsu)
            {
                AddYaku(Yaku.Ippatsu);
            }

            if (context.IsClosed && context.IsTsumo)
            {
                AddYaku(Yaku.MenzenTsumo);
            }

            if (context.IsHaitei)
            {
                AddYaku(Yaku.Haitei);
            }

            if (context.IsHoutei)
            {
                AddYaku(Yaku.Houtei);
            }

            if (context.IsRinshan)
            {
                AddYaku(Yaku.Rinshan);
            }

            if (context.IsChankan)
            {
                AddYaku(Yaku.Chankan);
            }

            if (allTiles.All(t => t.IsSimple))
            {
                AddYaku(Yaku.Tanyao);
            }

            if (CheckPinfu(decomposition, hand, context))
            {
                AddYaku(Yaku.Pinfu);
            }

            int iipeikoCount = CheckIipeiko(decomposition, context);
            if (iipeikoCount == 1)
            {
                AddYaku(Yaku.Iipeiko);
            }
            else if (iipeikoCount == 2)
            {
                AddYaku(Yaku.Ryanpeikou);
            }

            foreach (Yaku yaku in CheckYakuhai(decomposition, hand, context))
            {
                AddYaku(yaku);
            }

            bool isJunchan = CheckJunchan(decomposition, hand);
            bool isChanta = CheckChanta(decomposition, hand);
            if (isJunchan)
            {
                AddYaku(Yaku.Junchan);
            }
            else if (isChanta && !isJunchan)
            {
                AddYaku(Yaku.Chanta);
            }

            if (CheckHonroutou(allTiles))
            {
                AddYaku(Yaku.Honroutou);
            }

            if (CheckIttsu(decomposition, hand))
            {
                AddYaku(Yaku.Ittsu);
            }

            if (CheckSanShokuDoujun(decomposition, hand))
            {
                AddYaku(Yaku.SanShokuDoujun);
            }

            if (CheckSanShokuDoukou(decomposition, hand))
            {
                AddYaku(Yaku.SanShokuDoukou);
            }

            if (CheckToitoi(decomposition, hand))
            {
                AddYaku(Yaku.Toitoi);
            }

            if (CheckSanAnkou(decomposition, hand, context))
            {
                AddYaku(Yaku.SanAnkou);
            }

            if (hand.Melds.Count(m => m.IsKan) == 3)
            {
                AddYaku(Yaku.SanKantsu);
            }

            if (CheckShousangen(decomposition, hand))
            {
                AddYaku(Yaku.Shousangen);
            }

            if (decomposition.Pattern == DecompPattern.Chiitoitsu)
            {
                AddYaku(Yaku.Chiitoitsu);
            }

            bool isChinitsu = CheckChinitsu(allTiles);
            bool isHonitsu = CheckHonitsu(allTiles);
            if (isChinitsu)
            {
                AddYaku(Yaku.Chinitsu);
            }
            else if (isHonitsu && !isChinitsu)
            {
                AddYaku(Yaku.Honitsu);
            }

            return new YakuResult(normal, normal.Sum(y => y.Han), false, 0);
        }

        private static bool CheckKokushi(Decomposition decomp)
        {
            return decomp.Pattern == DecompPattern.Kokushi;
        }

        private static bool CheckSuuankou(Decomposition decomp, PlayerHand hand, WinContext context)
        {
            if (decomp.Pattern != DecompPattern.Regular)
            {
                return false;
            }

            return CountClosedTriplets(decomp, hand, context) == 4;
        }

        private static bool CheckDaisangen(Decomposition decomp, PlayerHand hand)
        {
            return CountDragonTriplets(decomp, hand) == 3;
        }

        private static bool CheckShousuushii(Decomposition decomp, PlayerHand hand)
        {
            if (decomp.Pattern != DecompPattern.Regular)
            {
                return false;
            }

            int windTriplets = CountWindTriplets(decomp, hand);
            bool isWindPair = decomp.Jantai >= 27 && decomp.Jantai <= 30;
            return windTriplets == 3 && isWindPair;
        }

        private static bool CheckDaisuushii(Decomposition decomp, PlayerHand hand)
        {
            return CountWindTriplets(decomp, hand) == 4;
        }

        private static bool CheckChurenpoutou(Decomposition decomp, PlayerHand hand, WinContext context)
        {
            if (!context.IsClosed)
            {
                return false;
            }

            IReadOnlyList<TileId> allTiles = hand.AllClosedTiles;
            if (allTiles.Count != 14)
            {
                return false;
            }

            TileSuit[] suits = allTiles.Select(t => t.Suit).Distinct().ToArray();
            if (suits.Length != 1 || suits[0] == TileSuit.Wind || suits[0] == TileSuit.Dragon)
            {
                return false;
            }

            var counts = new int[9];
            foreach (TileId tile in allTiles)
            {
                counts[tile.Rank - 1]++;
            }

            int extras = 0;
            if (counts[0] < 3)
            {
                return false;
            }

            extras += counts[0] - 3;
            if (counts[8] < 3)
            {
                return false;
            }

            extras += counts[8] - 3;
            for (int i = 1; i <= 7; i++)
            {
                if (counts[i] < 1)
                {
                    return false;
                }

                extras += counts[i] - 1;
            }

            return extras == 1;
        }

        private static bool CheckPinfu(Decomposition decomp, PlayerHand hand, WinContext context)
        {
            if (!context.IsClosed)
            {
                return false;
            }

            if (decomp.Pattern != DecompPattern.Regular)
            {
                return false;
            }

            if (decomp.Mentsu.Count != 4 || decomp.Mentsu.Any(m => !m.IsSequence))
            {
                return false;
            }

            int pairIndex = decomp.Jantai;
            if (pairIndex >= 31 && pairIndex <= 33)
            {
                return false;
            }

            if (pairIndex == 27 + context.SeatWind - 1)
            {
                return false;
            }

            if (pairIndex == 27 + context.RoundWind - 1)
            {
                return false;
            }

            bool isRyanmen = false;
            foreach (MentsuGroup mentsu in decomp.Mentsu)
            {
                int winIndex = context.WinTile.TypeIndex;
                if (mentsu.TypeIndices.Contains(winIndex))
                {
                    int idx = IndexOf(mentsu.TypeIndices, winIndex);
                    if (idx == 0)
                    {
                        int rank = winIndex % 9;
                        if (rank != 6)
                        {
                            isRyanmen = true;
                        }
                    }
                    else if (idx == 2)
                    {
                        int rank = winIndex % 9;
                        if (rank != 2)
                        {
                            isRyanmen = true;
                        }
                    }
                }
            }

            return isRyanmen;
        }

        private static int CheckIipeiko(Decomposition decomp, WinContext context)
        {
            if (!context.IsClosed)
            {
                return 0;
            }

            if (decomp.Pattern != DecompPattern.Regular)
            {
                return 0;
            }

            int[] sequences = decomp.Mentsu
                .Where(m => m.IsSequence)
                .Select(m => m.TypeIndices[0])
                .OrderBy(t => t)
                .ToArray();

            int pairs = 0;
            int i = 0;
            while (i < sequences.Length - 1)
            {
                if (sequences[i] == sequences[i + 1])
                {
                    pairs++;
                    i += 2;
                }
                else
                {
                    i++;
                }
            }

            return pairs;
        }

        private static List<Yaku> CheckYakuhai(Decomposition decomp, PlayerHand hand, WinContext context)
        {
            var yaku = new List<Yaku>();
            int[] triplets = decomp.Mentsu
                .Where(m => !m.IsSequence)
                .Select(m => m.TypeIndices[0])
                .Concat(hand.Melds.Where(m => !m.IsSequence).Select(m => m.Tiles[0].TypeIndex))
                .ToArray();

            foreach (int index in triplets)
            {
                if (index == 31)
                {
                    yaku.Add(Yaku.YakuhaiHaku);
                }
                else if (index == 32)
                {
                    yaku.Add(Yaku.YakuhaiHatsu);
                }
                else if (index == 33)
                {
                    yaku.Add(Yaku.YakuhaiChun);
                }

                if (index == 27 + context.SeatWind - 1)
                {
                    yaku.Add(Yaku.YakuhaiJikaze);
                }

                if (index == 27 + context.RoundWind - 1)
                {
                    yaku.Add(Yaku.YakuhaiBakaze);
                }
            }

            return yaku;
        }

        private static bool CheckJunchan(Decomposition decomp, PlayerHand hand)
        {
            if (decomp.Pattern != DecompPattern.Regular)
            {
                return false;
            }

            bool allMentsuHasTerminal = decomp.Mentsu.All(hasTerminal) &&
                                       hand.Melds.All(hasTerminalMeld);
            bool pairHasTerminal = IsTerminalIndex(decomp.Jantai);
            bool noHonors = CheckNoHonors(decomp, hand);
            return allMentsuHasTerminal && pairHasTerminal && noHonors;
        }

        private static bool CheckChanta(Decomposition decomp, PlayerHand hand)
        {
            if (decomp.Pattern != DecompPattern.Regular)
            {
                return false;
            }

            bool allMentsuHasTerminalOrHonor = decomp.Mentsu.All(hasTerminalOrHonor) &&
                                               hand.Melds.All(hasTerminalOrHonorMeld);
            bool pairHasTerminalOrHonor = IsTerminalOrHonorIndex(decomp.Jantai);
            bool hasHonors = !CheckNoHonors(decomp, hand);
            return allMentsuHasTerminalOrHonor && pairHasTerminalOrHonor && hasHonors;
        }

        private static bool CheckHonroutou(IReadOnlyList<TileId> allTiles)
        {
            return allTiles.All(t => t.IsTerminalOrHonor) &&
                   allTiles.Any(t => t.IsHonor) &&
                   allTiles.Any(t => t.IsTerminal);
        }

        private static bool CheckIttsu(Decomposition decomp, PlayerHand hand)
        {
            int[] sequences = decomp.Mentsu
                .Where(m => m.IsSequence)
                .Select(m => m.TypeIndices[0])
                .Concat(hand.Melds.Where(m => m.IsSequence)
                    .Select(m => m.Tiles.OrderBy(t => t.TypeIndex).First().TypeIndex))
                .ToArray();

            foreach (int suitBase in new[] { 0, 9, 18 })
            {
                if (sequences.Contains(suitBase) &&
                    sequences.Contains(suitBase + 3) &&
                    sequences.Contains(suitBase + 6))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CheckSanShokuDoujun(Decomposition decomp, PlayerHand hand)
        {
            for (int rank = 0; rank <= 6; rank++)
            {
                bool man = HasSequence(decomp, hand, rank);
                bool pin = HasSequence(decomp, hand, rank + 18);
                bool sou = HasSequence(decomp, hand, rank + 9);
                if (man && pin && sou)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasSequence(Decomposition decomp, PlayerHand hand, int firstTypeIndex)
        {
            if (decomp.Mentsu.Any(m => m.IsSequence && m.TypeIndices[0] == firstTypeIndex))
            {
                return true;
            }

            return hand.Melds.Any(m => m.IsSequence &&
                m.Tiles.OrderBy(t => t.TypeIndex).First().TypeIndex == firstTypeIndex);
        }

        private static bool CheckSanShokuDoukou(Decomposition decomp, PlayerHand hand)
        {
            int[] triplets = decomp.Mentsu
                .Where(m => !m.IsSequence)
                .Select(m => m.TypeIndices[0])
                .Concat(hand.Melds.Where(m => !m.IsSequence).Select(m => m.Tiles[0].TypeIndex))
                .ToArray();

            for (int rank = 0; rank <= 8; rank++)
            {
                if (triplets.Contains(rank) &&
                    triplets.Contains(rank + 9) &&
                    triplets.Contains(rank + 18))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CheckToitoi(Decomposition decomp, PlayerHand hand)
        {
            if (decomp.Pattern != DecompPattern.Regular)
            {
                return false;
            }

            bool hasSequence = decomp.Mentsu.Any(m => m.IsSequence) || hand.Melds.Any(m => m.IsSequence);
            return !hasSequence;
        }

        private static bool CheckSanAnkou(Decomposition decomp, PlayerHand hand, WinContext context)
        {
            if (decomp.Pattern != DecompPattern.Regular)
            {
                return false;
            }

            return CountClosedTriplets(decomp, hand, context) == 3;
        }

        private static bool CheckShousangen(Decomposition decomp, PlayerHand hand)
        {
            int dragons = CountDragonTriplets(decomp, hand);
            bool pairIsDragon = decomp.Jantai >= 31 && decomp.Jantai <= 33;
            return dragons == 2 && pairIsDragon;
        }

        private static bool CheckHonitsu(IReadOnlyList<TileId> allTiles)
        {
            bool hasHonor = allTiles.Any(t => t.IsHonor);
            if (!hasHonor)
            {
                return false;
            }

            int[] suits = allTiles.Where(t => t.IsNumberTile).Select(t => (int)t.Suit).Distinct().ToArray();
            return suits.Length == 1;
        }

        private static bool CheckChinitsu(IReadOnlyList<TileId> allTiles)
        {
            TileSuit[] suits = allTiles.Select(t => t.Suit).Distinct().ToArray();
            return suits.Length == 1 && suits[0] != TileSuit.Wind && suits[0] != TileSuit.Dragon;
        }

        private static int CountClosedTriplets(Decomposition decomp, PlayerHand hand, WinContext context)
        {
            int count = 0;
            foreach (MentsuGroup mentsu in decomp.Mentsu)
            {
                if (!mentsu.IsSequence)
                {
                    if (!context.IsTsumo && mentsu.TypeIndices[0] == context.WinTile.TypeIndex)
                    {
                        continue;
                    }

                    count++;
                }
            }

            count += hand.Melds.Count(m => m.Type == MeldType.ClosedKan);
            return count;
        }

        private static int CountDragonTriplets(Decomposition decomp, PlayerHand hand)
        {
            return decomp.Mentsu
                .Where(m => !m.IsSequence)
                .Select(m => m.TypeIndices[0])
                .Concat(hand.Melds.Where(m => !m.IsSequence).Select(m => m.Tiles[0].TypeIndex))
                .Count(index => index >= 31 && index <= 33);
        }

        private static int CountWindTriplets(Decomposition decomp, PlayerHand hand)
        {
            return decomp.Mentsu
                .Where(m => !m.IsSequence)
                .Select(m => m.TypeIndices[0])
                .Concat(hand.Melds.Where(m => !m.IsSequence).Select(m => m.Tiles[0].TypeIndex))
                .Count(index => index >= 27 && index <= 30);
        }

        private static bool IsTerminalIndex(int index)
        {
            return index >= 0 && index <= 26 && (index % 9 == 0 || index % 9 == 8);
        }

        private static bool IsTerminalOrHonorIndex(int index)
        {
            return IsTerminalIndex(index) || (index >= 27 && index <= 33);
        }

        private static bool hasTerminal(MentsuGroup mentsu)
        {
            return mentsu.TypeIndices.Any(IsTerminalIndex);
        }

        private static bool hasTerminalOrHonor(MentsuGroup mentsu)
        {
            return mentsu.TypeIndices.Any(IsTerminalOrHonorIndex);
        }

        private static bool hasTerminalMeld(Meld meld)
        {
            return meld.Tiles.Any(t => t.IsTerminal);
        }

        private static bool hasTerminalOrHonorMeld(Meld meld)
        {
            return meld.Tiles.Any(t => t.IsTerminalOrHonor);
        }

        private static bool CheckNoHonors(Decomposition decomp, PlayerHand hand)
        {
            if (decomp.Jantai >= 27 && decomp.Jantai <= 33)
            {
                return false;
            }

            if (decomp.Mentsu.Any(m => m.TypeIndices.Any(i => i >= 27 && i <= 33)))
            {
                return false;
            }

            if (hand.Melds.Any(m => m.Tiles.Any(t => t.IsHonor)))
            {
                return false;
            }

            return true;
        }

        private static int IndexOf(IReadOnlyList<int> values, int value)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] == value)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
