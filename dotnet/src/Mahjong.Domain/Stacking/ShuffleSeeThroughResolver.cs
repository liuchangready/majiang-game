using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mahjong.Domain.Stacking
{
    /// <summary>
    /// 看破抵消后的有效方案解析（V4 <c>ShufflePreparationHandler</c> 冻结规则的纯函数移植）。
    ///
    /// 规则（V4 engine/src/main/kotlin/com/majiang/authority/cheat/ShufflePreparationHandler.kt
    /// “准备截止时冻结看破席位集合 K”段）：
    /// - K = 看破启用时 seeThrough==true 的席位集合，在准备截止时一次性冻结；
    /// - 对每个席位：只要 K 中存在“其他席位”（any { it != plan.seat }），该席位的
    ///   有效方案被整效抵消（target→null 且 seeThrough→false）；
    /// - 未被抵消的席位仅清除 seeThrough 标记（看破效果已由费用结算，不再进入牌山算法）；
    /// - 原始方案与有效方案严格分离：费用仍由原始方案计算，
    ///   有效施术人数 n 由有效方案中 target 非空的席位计数。
    /// 纯函数：无时钟、无随机、无副作用。
    /// </summary>
    public static class ShuffleSeeThroughResolver
    {
        /// <summary>
        /// 解析四席原始方案与看破抵消后的有效方案。
        /// selections 按座位索引必须恰好 4 项，null 视为跳过（空选择）。
        /// </summary>
        public static ShuffleSeeThroughResolution Resolve(IReadOnlyList<ShuffleSelection?> selections, bool seeThroughEnabled)
        {
            if (selections == null)
            {
                throw new ArgumentNullException(nameof(selections));
            }

            if (selections.Count != 4)
            {
                throw new ArgumentException(
                    "selections must have exactly 4 entries (one per seat), got " +
                    selections.Count.ToString(CultureInfo.InvariantCulture) + ".",
                    nameof(selections));
            }

            var frozenSeeThroughSeats = new List<int>();
            if (seeThroughEnabled)
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    if (selections[seat] != null && selections[seat]!.SeeThrough)
                    {
                        frozenSeeThroughSeats.Add(seat);
                    }
                }
            }

            var originalPlans = new List<SeatShufflePlan>(4);
            var effectivePlans = new List<SeatShufflePlan>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                ShuffleSelection selection = selections[seat] ?? new ShuffleSelection();
                originalPlans.Add(new SeatShufflePlan(seat, selection));

                bool suppressed = false;
                foreach (int other in frozenSeeThroughSeats)
                {
                    if (other != seat)
                    {
                        suppressed = true;
                        break;
                    }
                }

                ShuffleSelection effective = suppressed
                    ? new ShuffleSelection(null, false)
                    : selection.WithoutSeeThrough();
                effectivePlans.Add(new SeatShufflePlan(seat, effective));
            }

            return new ShuffleSeeThroughResolution(originalPlans, effectivePlans, frozenSeeThroughSeats);
        }
    }

    /// <summary>看破解析结果：原始方案（计费用）与有效方案（进牌山算法）严格分离。</summary>
    public sealed class ShuffleSeeThroughResolution
    {
        internal ShuffleSeeThroughResolution(
            IReadOnlyList<SeatShufflePlan> originalPlans,
            IReadOnlyList<SeatShufflePlan> effectivePlans,
            IReadOnlyList<int> frozenSeeThroughSeats)
        {
            OriginalPlans = originalPlans;
            EffectivePlans = effectivePlans;
            FrozenSeeThroughSeats = frozenSeeThroughSeats;
        }

        /// <summary>按座位 0..3 的原始方案；费用账本以本列表为准。</summary>
        public IReadOnlyList<SeatShufflePlan> OriginalPlans { get; }

        /// <summary>按座位 0..3 的有效方案；牌山合成以本列表为准。</summary>
        public IReadOnlyList<SeatShufflePlan> EffectivePlans { get; }

        /// <summary>冻结的看破席位集合（升序）。</summary>
        public IReadOnlyList<int> FrozenSeeThroughSeats { get; }

        /// <summary>有效施术人数 n：有效方案中 target 非空的席位计数。</summary>
        public int EffectiveCasterCount
        {
            get
            {
                int count = 0;
                foreach (SeatShufflePlan plan in EffectivePlans)
                {
                    if (plan.Selection.Target.HasValue)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }
}
