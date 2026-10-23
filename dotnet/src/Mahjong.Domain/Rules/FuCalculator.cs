using System;
using System.Collections.Generic;
using System.Linq;

namespace Mahjong.Domain.Rules
{
    public sealed class FuLine
    {
        public FuLine(string description, int fu)
        {
            Description = description ?? throw new ArgumentNullException(nameof(description));
            Fu = fu;
        }

        public string Description { get; }

        public int Fu { get; }
    }

    public sealed class FuBreakdown
    {
        public FuBreakdown(
            IEnumerable<FuLine> lines,
            int subtotal,
            int roundedUpTo,
            int finalFu,
            string? specialNote = null)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            Lines = Array.AsReadOnly(lines.ToArray());
            Subtotal = subtotal;
            RoundedUpTo = roundedUpTo;
            FinalFu = finalFu;
            SpecialNote = specialNote;
        }

        public IReadOnlyList<FuLine> Lines { get; }

        public int Subtotal { get; }

        public int RoundedUpTo { get; }

        public int FinalFu { get; }

        public string? SpecialNote { get; }
    }

    /// <summary>
    /// Port of V4 <c>FuCalculator</c>. The <c>+4</c> double-wind pair is deliberate:
    /// V4 code adds both seat-wind and round-wind +2 even though its comment says +2.
    /// </summary>
    public static class FuCalculator
    {
        public static FuBreakdown CalculateBreakdown(
            Decomposition decomposition,
            TileId winTile,
            bool isTsumo,
            bool isClosed,
            int seatWind,
            int roundWind,
            IEnumerable<Meld>? openMelds = null)
        {
            if (decomposition == null)
            {
                throw new ArgumentNullException(nameof(decomposition));
            }

            if (seatWind < 1 || seatWind > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(seatWind), seatWind, "seatWind must be 1..4.");
            }

            if (roundWind < 1 || roundWind > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(roundWind), roundWind, "roundWind must be 1..4.");
            }

            Meld[] melds = openMelds == null ? Array.Empty<Meld>() : openMelds.ToArray();
            int winTypeIndex = winTile.TypeIndex;

            if (decomposition.Pattern == DecompPattern.Chiitoitsu)
            {
                return new FuBreakdown(
                    new[] { new FuLine("七对子固定", 25) },
                    25,
                    25,
                    25,
                    "七对子 25符");
            }

            if (decomposition.Pattern == DecompPattern.Kokushi)
            {
                return new FuBreakdown(
                    new[] { new FuLine("国士无双固定", 30) },
                    30,
                    30,
                    30,
                    "国士无双 30符");
            }

            if (isTsumo && isClosed && IsPinfu(decomposition, winTypeIndex, seatWind, roundWind))
            {
                return new FuBreakdown(
                    new[] { new FuLine("底符", 20) },
                    20,
                    20,
                    20,
                    "平和自摸 20符");
            }

            var lines = new List<FuLine>();
            int baseFu = isTsumo ? 20 : (isClosed ? 30 : 20);
            lines.Add(new FuLine("底符", baseFu));

            foreach (MentsuGroup mentsu in decomposition.Mentsu)
            {
                bool isOpen = !isTsumo && !mentsu.IsSequence &&
                              mentsu.TypeIndices[0] == winTypeIndex &&
                              decomposition.Jantai != winTypeIndex;
                int fu = CalculateMentsuFu(mentsu, isOpen);
                if (fu > 0)
                {
                    string tileName = TileCompactName(mentsu.TypeIndices[0]);
                    string description = (isOpen ? "明刻" : "暗刻") + tileName;
                    lines.Add(new FuLine(description, fu));
                }
            }

            foreach (Meld meld in melds)
            {
                int fu = CalculateMeldFu(meld);
                if (fu > 0)
                {
                    string tileName = TileCompactName(meld.Tiles[0].TypeIndex);
                    string description;
                    switch (meld.Type)
                    {
                        case MeldType.Chi:
                            description = "吃" + tileName;
                            break;
                        case MeldType.Pon:
                            description = "明刻" + tileName;
                            break;
                        case MeldType.OpenKan:
                            description = "明杠" + tileName;
                            break;
                        case MeldType.ClosedKan:
                            description = "暗杠" + tileName;
                            break;
                        case MeldType.AddedKan:
                            description = "加杠" + tileName;
                            break;
                        default:
                            throw new InvalidOperationException("Unsupported meld type: " + meld.Type);
                    }

                    lines.Add(new FuLine(description, fu));
                }
            }

            if (decomposition.Jantai >= 0)
            {
                int fu = CalculateJantaiFu(decomposition.Jantai, seatWind, roundWind);
                if (fu > 0)
                {
                    string tileName = TileCompactName(decomposition.Jantai);
                    lines.Add(new FuLine("雀头" + tileName, fu));
                }
            }

            string? waitDescription = DetermineWaitDescription(decomposition, winTypeIndex);
            if (waitDescription != null)
            {
                lines.Add(new FuLine(waitDescription, 2));
            }

            if (isTsumo)
            {
                lines.Add(new FuLine("自摸", 2));
            }

            int subtotal = lines.Sum(line => line.Fu);
            int rounded = RoundUpTo10(subtotal);
            int finalFu;
            string? specialNote;
            if (!isTsumo && rounded < 30)
            {
                finalFu = 30;
                specialNote = "荣和 30符保底";
            }
            else
            {
                finalFu = rounded;
                specialNote = null;
            }

            return new FuBreakdown(lines, subtotal, rounded, finalFu, specialNote);
        }

        public static int Calculate(
            Decomposition decomposition,
            TileId winTile,
            bool isTsumo,
            bool isClosed,
            int seatWind,
            int roundWind,
            IEnumerable<Meld>? openMelds = null)
        {
            return CalculateBreakdown(
                decomposition,
                winTile,
                isTsumo,
                isClosed,
                seatWind,
                roundWind,
                openMelds).FinalFu;
        }

        public static bool IsPinfuShape(
            Decomposition decomposition,
            TileId winTile,
            int seatWind,
            int roundWind)
        {
            if (decomposition == null)
            {
                throw new ArgumentNullException(nameof(decomposition));
            }

            return IsPinfu(decomposition, winTile.TypeIndex, seatWind, roundWind);
        }

        private static int CalculateMentsuFu(MentsuGroup mentsu, bool isOpen)
        {
            if (mentsu.IsSequence)
            {
                return 0;
            }

            int typeIndex = mentsu.TypeIndices[0];
            bool isTerminalOrHonor = TileFacts.IsTerminalOrHonor(typeIndex);
            int baseFu = isTerminalOrHonor ? 4 : 2;
            return isOpen ? baseFu : baseFu * 2;
        }

        private static int CalculateMeldFu(Meld meld)
        {
            bool isYaochu = meld.Tiles[0].IsTerminalOrHonor;
            switch (meld.Type)
            {
                case MeldType.Chi:
                    return 0;
                case MeldType.Pon:
                    return isYaochu ? 4 : 2;
                case MeldType.OpenKan:
                    return isYaochu ? 16 : 8;
                case MeldType.ClosedKan:
                    return isYaochu ? 32 : 16;
                case MeldType.AddedKan:
                    return isYaochu ? 16 : 8;
                default:
                    throw new InvalidOperationException("Unsupported meld type: " + meld.Type);
            }
        }

        private static int CalculateJantaiFu(int jantaiIndex, int seatWind, int roundWind)
        {
            if (jantaiIndex >= 31 && jantaiIndex <= 33)
            {
                return 2;
            }

            if (jantaiIndex >= 27 && jantaiIndex <= 30)
            {
                int windRank = jantaiIndex - 27 + 1;
                int fu = 0;
                if (windRank == seatWind)
                {
                    fu += 2;
                }

                if (windRank == roundWind)
                {
                    fu += 2;
                }

                return fu;
            }

            return 0;
        }

        private static string? DetermineWaitDescription(Decomposition decomposition, int winTypeIndex)
        {
            if (decomposition.Jantai == winTypeIndex)
            {
                return "听牌单骑";
            }

            foreach (MentsuGroup mentsu in decomposition.Mentsu)
            {
                if (!mentsu.TypeIndices.Contains(winTypeIndex))
                {
                    continue;
                }

                if (mentsu.IsSequence)
                {
                    int[] sorted = mentsu.TypeIndices.OrderBy(t => t).ToArray();
                    int suitBase = (sorted[0] / 9) * 9;
                    int[] ranks = sorted.Select(t => t - suitBase).ToArray();
                    if (ranks[1] == winTypeIndex - suitBase && ranks[0] + 2 == ranks[2])
                    {
                        return "听牌嵌张";
                    }

                    int winRank = winTypeIndex - suitBase;
                    if (ranks[0] == 0 && ranks[2] == 2 && winRank == 2)
                    {
                        return "听牌边张";
                    }

                    if (ranks[0] == 6 && ranks[2] == 8 && winRank == 6)
                    {
                        return "听牌边张";
                    }
                }
            }

            return null;
        }

        private static bool IsPinfu(Decomposition decomposition, int winTypeIndex, int seatWind, int roundWind)
        {
            if (decomposition.Pattern != DecompPattern.Regular)
            {
                return false;
            }

            if (decomposition.Mentsu.Any(m => !m.IsSequence))
            {
                return false;
            }

            if (CalculateJantaiFu(decomposition.Jantai, seatWind, roundWind) > 0)
            {
                return false;
            }

            foreach (MentsuGroup mentsu in decomposition.Mentsu)
            {
                if (!mentsu.TypeIndices.Contains(winTypeIndex))
                {
                    continue;
                }

                if (mentsu.IsSequence)
                {
                    int[] sorted = mentsu.TypeIndices.OrderBy(t => t).ToArray();
                    int suitBase = (sorted[0] / 9) * 9;
                    int winRank = winTypeIndex - suitBase;
                    int minRank = sorted[0] - suitBase;
                    int maxRank = sorted[2] - suitBase;

                    if (winRank == minRank && maxRank < 8)
                    {
                        return true;
                    }

                    if (winRank == maxRank && minRank > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string TileCompactName(int typeIndex)
        {
            if (typeIndex >= 0 && typeIndex <= 8)
            {
                return (typeIndex + 1) + "m";
            }

            if (typeIndex >= 9 && typeIndex <= 17)
            {
                return (typeIndex - 9 + 1) + "s";
            }

            if (typeIndex >= 18 && typeIndex <= 26)
            {
                return (typeIndex - 18 + 1) + "p";
            }

            if (typeIndex >= 27 && typeIndex <= 30)
            {
                return new[] { "東", "南", "西", "北" }[typeIndex - 27];
            }

            if (typeIndex >= 31 && typeIndex <= 33)
            {
                return new[] { "白", "發", "中" }[typeIndex - 31];
            }

            return "?";
        }

        private static int RoundUpTo10(int value)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "fu cannot be negative.");
            }

            return checked(((value + 9) / 10) * 10);
        }
    }
}
