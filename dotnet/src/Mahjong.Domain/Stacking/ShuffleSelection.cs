using System;

namespace Mahjong.Domain.Stacking
{
    /// <summary>
    /// 玩家洗牌千术目标选择方案（统一码牌千术；V4 <c>com.majiang.stacking.ShuffleSelection</c>）。
    ///
    /// 准备阶段只提交一次、只选一个目标。目标同时作用于起手发牌与本局全部正常顺位预计摸牌槽。
    /// 费用与目标校验由权威常量决定，不接受客户端自报费用。
    /// </summary>
    public sealed class ShuffleSelection
    {
        /// <summary>统一码牌目标固定费用（权威校验使用该常量）。</summary>
        public const int TargetCost = 30;

        /// <summary>看破固定费用（权威校验使用该常量）。</summary>
        public const int SeeThroughCost = 10;

        /// <summary>准备阶段单方案合法费用上限：30 + 10 = 40。</summary>
        public const int MaxCost = TargetCost + SeeThroughCost;

        public ShuffleSelection(ShuffleTarget? target = null, bool seeThrough = false)
        {
            if (target.HasValue)
            {
                ShuffleTargetRules.RequireDefined(target.Value);
            }

            Target = target;
            SeeThrough = seeThrough;
        }

        /// <summary>唯一做牌目标；null 表示本局不做牌（跳过目标，0 点）。</summary>
        public ShuffleTarget? Target { get; }

        /// <summary>本局准备阶段是否使用看破（10 点）。</summary>
        public bool SeeThrough { get; }

        /// <summary>是否有任何生效千术（仅选择看破也是合法非空方案）。</summary>
        public bool IsActive => Target.HasValue || SeeThrough;

        /// <summary>
        /// 计算本方案消耗的出千点数：统一目标 30 点/局，看破 10 点/局，可叠加为 40 点；跳过 0 点。
        /// </summary>
        public int Cost()
        {
            return (Target.HasValue ? TargetCost : 0) + (SeeThrough ? SeeThroughCost : 0);
        }

        /// <summary>返回替换目标的副本。</summary>
        public ShuffleSelection WithTarget(ShuffleTarget? target)
        {
            if (Target == target)
            {
                return this;
            }

            return new ShuffleSelection(target, SeeThrough);
        }

        /// <summary>返回清除看破标记的副本（看破抵消与生效回填使用）。</summary>
        public ShuffleSelection WithoutSeeThrough()
        {
            if (!SeeThrough)
            {
                return this;
            }

            return new ShuffleSelection(Target, false);
        }

        public override string ToString()
        {
            return "ShuffleSelection(target=" + (Target.HasValue ? Target.Value.ToString() : "null") +
                   ", seeThrough=" + (SeeThrough ? "true" : "false") + ")";
        }
    }

    /// <summary>
    /// 绑定座位的洗牌千术方案（V4 <c>com.majiang.stacking.SeatShufflePlan</c>）。
    /// </summary>
    public sealed class SeatShufflePlan
    {
        public SeatShufflePlan(int seat, ShuffleSelection selection)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(seat),
                    seat,
                    "seat must be 0..3, got " + seat.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
            }

            Seat = seat;
            Selection = selection ?? throw new ArgumentNullException(nameof(selection));
        }

        /// <summary>座位索引 0..3。</summary>
        public int Seat { get; }

        /// <summary>玩家选择的千术方案。</summary>
        public ShuffleSelection Selection { get; }

        public override string ToString() => "SeatShufflePlan(seat=" + Seat + ", " + Selection + ")";
    }
}
