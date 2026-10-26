using System;
using System.Collections.Generic;

namespace Mahjong.Domain.Stacking
{
    /// <summary>
    /// 洗牌物理槽位划分计算器（V4 <c>com.majiang.stacking.ShuffleSlots</c> 的逐值移植）。
    ///
    /// 物理守恒 136 张牌山区间分布：
    /// - 0..52: 起手发牌区（共 53 张）：4x3 发牌轮次（每人 4 张 x 3 轮）+
    ///   单张轮次（每人 1 张，槽 48..51）+ 庄家第 14 张 Extra（槽 52）；
    /// - 53..121: 正常顺位后续摸牌区（共 69 张）；
    /// - 122..135: 王牌区（Dead Wall，14 张），绝对不可作为做牌目标或替换来源。
    /// 纯函数：非法座位 / 负数轮数直接抛出，零副作用。
    /// </summary>
    public static class ShuffleSlots
    {
        public const int TotalWallTiles = 136;
        public const int MainWallTiles = 122;
        public const int DeadWallStart = 122;
        public const int DeadWallEnd = 135;
        public const int InitialDealTiles = 53;

        /// <summary>默认预计摸牌轮数：覆盖 53..121 中本座位的全部 17/18 个预计槽。</summary>
        public const int DefaultPlannedDraws = 18;

        /// <summary>
        /// 计算指定座位玩家在起手发牌阶段分配到的绝对牌山槽位 (0..52)。
        /// 庄家 14 个槽位（末槽 52），子家 13 个槽位（末槽 48+k）。
        /// </summary>
        public static IReadOnlyList<int> Initial(int dealerSeat, int seat)
        {
            RequireSeat(dealerSeat, nameof(dealerSeat));
            RequireSeat(seat, nameof(seat));

            int k = (seat - dealerSeat + 4) % 4;
            var slots = new List<int>(k == 0 ? 14 : 13);

            // 3 轮每人 4 张 (0..47)
            for (int round = 0; round < 3; round++)
            {
                int baseSlot = (round * 16) + (4 * k);
                for (int i = 0; i < 4; i++)
                {
                    slots.Add(baseSlot + i);
                }
            }

            // 单张发牌轮 (48..51)
            slots.Add(48 + k);

            // 庄家第 14 张 extra (52)
            if (k == 0)
            {
                slots.Add(52);
            }

            return slots.AsReadOnly();
        }

        /// <summary>
        /// 计算指定座位玩家在无碰杠理想顺位下，后续前 <paramref name="count"/> 次预计摸牌的
        /// 绝对牌山槽位 (53..121)。绝不包含 122..135 王牌区；超出 121 自动截断。
        /// </summary>
        public static IReadOnlyList<int> Draws(int dealerSeat, int seat, int count)
        {
            RequireSeat(dealerSeat, nameof(dealerSeat));
            RequireSeat(seat, nameof(seat));
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    count,
                    "count must be non-negative, got " + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
            }

            // 顺位偏移：庄家首轮已摸第 14 张牌，下家 (dealer+1)%4 先摸第 53 张，随后顺时针轮转
            int turnOffset = (seat - dealerSeat - 1 + 4) % 4;
            var slots = new List<int>();

            for (int r = 0; r < count; r++)
            {
                int slot = 53 + (4 * r) + turnOffset;
                if (slot >= 53 && slot <= 121)
                {
                    slots.Add(slot);
                }
            }

            return slots.AsReadOnly();
        }

        private static void RequireSeat(int seat, string paramName)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(
                    paramName,
                    seat,
                    paramName + " must be 0..3, got " + seat.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
            }
        }
    }
}
