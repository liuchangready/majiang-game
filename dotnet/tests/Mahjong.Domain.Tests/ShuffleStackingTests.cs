using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Domain;
using Mahjong.Domain.Stacking;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// P1.5 码牌纯单元验收：物理槽位、34 维需求矩阵、档位标定表、选择费用、
    /// 看破抵消解析与随机记录校验。
    ///
    /// 预期值来源：V4 engine/src/test/kotlin/com/majiang/v3/ShuffleAlgorithmAcceptanceTest.kt
    /// 的冻结数值、独立推导的公式期望，以及 ShufflePreparationHandler.kt 冻结规则的手工展开，
    /// 绝不用被测实现本身作为 oracle。
    /// </summary>
    public class ShuffleStackingTests
    {
        // ------------------------------------------------------------------
        // 槽位分配（V4 验收测试 `physical slots follow actual dealer...` 的移植，
        // 期望值在测试内独立构造，不调用实现生成）
        // ------------------------------------------------------------------

        [Fact]
        public void InitialSlotsFollowV4DealOrderAndNeverTouchDrawOrDeadWall()
        {
            for (int dealer = 0; dealer <= 3; dealer++)
            {
                var dealt = new List<int>();
                for (int seat = 0; seat <= 3; seat++)
                {
                    IReadOnlyList<int> slots = ShuffleSlots.Initial(dealer, seat);
                    int k = (seat - dealer + 4) % 4;

                    // 独立期望：庄家 14 槽（末槽 52），子家 13 槽（末槽 48+k）
                    int expectedSize = seat == dealer ? 14 : 13;
                    int expectedLast = seat == dealer ? 52 : (48 + k);
                    Assert.Equal(expectedSize, slots.Count);
                    Assert.Equal(expectedLast, slots.Last());

                    // 独立期望：3 轮每人 4 张（round*16 + 4k + i）+ 单张槽 48+k
                    var expected = new List<int>();
                    for (int round = 0; round < 3; round++)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            expected.Add((round * 16) + (4 * k) + i);
                        }
                    }

                    expected.Add(48 + k);
                    if (k == 0)
                    {
                        expected.Add(52);
                    }

                    Assert.Equal(expected, slots.ToList());
                    dealt.AddRange(slots);
                }

                // 四席并集恰为 0..52，共 53 张（6398f77 物理守恒）
                Assert.Equal(Enumerable.Range(0, 53).ToList(), dealt.Distinct().OrderBy(x => x).ToList());
                Assert.Equal(53, dealt.Count);
            }
        }

        [Fact]
        public void DrawSlotsFollowTurnOrderAndNeverTouchDeadWall()
        {
            for (int dealer = 0; dealer <= 3; dealer++)
            {
                for (int seat = 0; seat <= 3; seat++)
                {
                    IReadOnlyList<int> slots = ShuffleSlots.Draws(dealer, seat, 18);

                    // 独立期望：53 + 4r + turnOffset，直到 121 截断
                    int turnOffset = (seat - dealer - 1 + 4) % 4;
                    var expected = new List<int>();
                    for (int slot = 53 + turnOffset; slot <= 121; slot += 4)
                    {
                        expected.Add(slot);
                    }

                    Assert.Equal(expected, slots.ToList());
                    Assert.All(slots, slot => Assert.InRange(slot, 53, 121));
                    Assert.DoesNotContain(slots, slot => slot >= ShuffleSlots.DeadWallStart);
                }
            }
        }

        [Fact]
        public void DrawSlotsTruncateBeyondWallEndAndRejectIllegalArguments()
        {
            // 超出实际槽量的轮数按 121 截断：dealer=0 时 seat=0 的偏移是 3，最多 17 槽
            Assert.Equal(
                ShuffleSlots.Draws(0, 0, 18).ToList(),
                ShuffleSlots.Draws(0, 0, 50).ToList());
            Assert.Empty(ShuffleSlots.Draws(0, 0, 0));

            Assert.Throws<ArgumentOutOfRangeException>(() => ShuffleSlots.Initial(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ShuffleSlots.Initial(4, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ShuffleSlots.Initial(0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => ShuffleSlots.Initial(0, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => ShuffleSlots.Draws(-1, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => ShuffleSlots.Draws(0, 4, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => ShuffleSlots.Draws(0, 0, -1));
        }

        // ------------------------------------------------------------------
        // 34 维需求矩阵（V4 `resource matrix is normalized...` 冻结数值移植）
        // ------------------------------------------------------------------

        [Fact]
        public void DemandVectorsAreNormalizedAndOverlapsMatchFrozenValues()
        {
            var allTargets = new List<ShuffleTarget>
            {
                ShuffleTarget.Kokushi,
                ShuffleTarget.ChinitsuMan,
                ShuffleTarget.Daisangen,
                ShuffleTarget.Daisuushii,
                ShuffleTarget.ChinitsuSou,
                ShuffleTarget.ChinitsuPin
            };

            foreach (ShuffleTarget a in allTargets)
            {
                double[] demand = ShuffleTargets.Demand(a);
                Assert.Equal(34, demand.Length);
                Assert.Equal(1.0, demand.Sum(), 8);
                Assert.All(demand, v => Assert.True(double.IsFinite(v) && v >= 0.0));
                Assert.Equal(1.0, ShuffleTargets.Overlap(a, a), 8);
                foreach (ShuffleTarget b in allTargets)
                {
                    Assert.Equal(ShuffleTargets.Overlap(a, b), ShuffleTargets.Overlap(b, a), 8);
                }
            }

            // 冻结硬值（V4 验收测试第 63-67 行）：完全不同花色零重叠；
            // 清一色与国士共享 1万+9万 → 2/sqrt(117)；大三元共享三未有 → 3/sqrt(39)；
            // 大四喜共享四风 → 4/sqrt(52)；大三元与大四喜零重叠。
            Assert.Equal(0.0, ShuffleTargets.Overlap(ShuffleTarget.ChinitsuMan, ShuffleTarget.ChinitsuPin), 8);
            Assert.Equal(2.0 / Math.Sqrt(117.0), ShuffleTargets.Overlap(ShuffleTarget.ChinitsuMan, ShuffleTarget.Kokushi), 8);
            Assert.Equal(3.0 / Math.Sqrt(39.0), ShuffleTargets.Overlap(ShuffleTarget.Daisangen, ShuffleTarget.Kokushi), 8);
            Assert.Equal(4.0 / Math.Sqrt(52.0), ShuffleTargets.Overlap(ShuffleTarget.Daisuushii, ShuffleTarget.Kokushi), 8);
            Assert.Equal(0.0, ShuffleTargets.Overlap(ShuffleTarget.Daisangen, ShuffleTarget.Daisuushii), 8);

            Assert.Equal(1.0, ShuffleTargets.Combined(allTargets).Sum(), 8);
            Assert.All(ShuffleTargets.Combined(new List<ShuffleTarget>()), v => Assert.Equal(0.0, v));

            // 国士需求恰覆盖 13 个幺九 typeIndex，每个权重 1/13（独立手算冻结值）
            double[] kokushi = ShuffleTargets.Demand(ShuffleTarget.Kokushi);
            int[] expectedKokushi = { 0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33 };
            for (int i = 0; i < 34; i++)
            {
                if (expectedKokushi.Contains(i))
                {
                    Assert.Equal(1.0 / 13.0, kokushi[i], 12);
                }
                else
                {
                    Assert.Equal(0.0, kokushi[i]);
                }
            }

            // 防御性副本：改写返回值不得污染后续调用
            double[] mutable = ShuffleTargets.Demand(ShuffleTarget.Kokushi);
            Array.Fill(mutable, 0.0);
            Assert.Equal(1.0, ShuffleTargets.Demand(ShuffleTarget.Kokushi).Sum(), 8);
        }

        [Fact]
        public void DemandRejectsUnknownEnumValue()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ShuffleTargets.Demand((ShuffleTarget)99));
            Assert.False(ShuffleTargetRules.IsDefined((ShuffleTarget)99));
            Assert.True(ShuffleTargetRules.IsDefined(ShuffleTarget.ChinitsuPin));
        }

        // ------------------------------------------------------------------
        // 选择费用（ShuffleSelection.kt 冻结常量）
        // ------------------------------------------------------------------

        [Fact]
        public void SelectionCostsMatchFrozenV4Values()
        {
            Assert.Equal(30, ShuffleSelection.TargetCost);
            Assert.Equal(10, ShuffleSelection.SeeThroughCost);
            Assert.Equal(40, ShuffleSelection.MaxCost);

            Assert.Equal(0, new ShuffleSelection().Cost());
            Assert.Equal(30, new ShuffleSelection(ShuffleTarget.ChinitsuMan).Cost());
            Assert.Equal(10, new ShuffleSelection(null, true).Cost());
            Assert.Equal(40, new ShuffleSelection(ShuffleTarget.Daisangen, true).Cost());

            Assert.False(new ShuffleSelection().IsActive);
            Assert.True(new ShuffleSelection(null, true).IsActive);
            Assert.True(new ShuffleSelection(ShuffleTarget.Kokushi).IsActive);

            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleSelection((ShuffleTarget)99));
        }

        // ------------------------------------------------------------------
        // 档位标定表（V4 `tier configuration is named validated and bounded`、
        // `global strength cap applies...` 移植；数值为 ShuffleConfig.kt 冻结标定）
        // ------------------------------------------------------------------

        [Fact]
        public void TierTablesMatchFrozenV4Calibration()
        {
            var config = new ShuffleConfig(enabled: true);

            Assert.Equal(18, config.PlannedDraws);
            Assert.Equal(new ShuffleTier(0.52, 1, 4), config.TierFor(1));
            Assert.Equal(new ShuffleTier(0.52, 1, 4), config.TierFor(1, ShuffleTarget.ChinitsuMan));
            Assert.Equal(new ShuffleTier(0.52, 2, 2), config.TierFor(2));
            Assert.Equal(new ShuffleTier(0.65, 3, 5), config.TierFor(3));
            Assert.Null(config.TierFor(4));

            // 目标覆盖档：国士 n=1 强度 0.06（冻结标定）
            Assert.Equal(0.06, config.TierFor(1, ShuffleTarget.Kokushi)!.Value.Strength, 9);
            Assert.Equal(new ShuffleTier(0.58, 1, 5), config.TierFor(1, ShuffleTarget.ChinitsuPin));
            Assert.Equal(new ShuffleTier(0.75, 5, 7), config.TierFor(2, ShuffleTarget.Daisangen));

            // 全局 strength 上限：0.4 削弱默认档；0.0 压过一切档位表
            Assert.Equal(new ShuffleTier(0.4, 1, 4), new ShuffleConfig(enabled: true, strength: 0.4).TierFor(1));
            Assert.Equal(0.0, new ShuffleConfig(enabled: true, strength: 0.0).TierFor(1)!.Value.Strength);
            Assert.Equal(new ShuffleTier(0.06, 1, 1).WithStrengthCap(0.4), new ShuffleConfig(enabled: true, strength: 0.4).TierFor(1, ShuffleTarget.Kokushi));

            // 自定义档位替换与目标覆盖回退
            ShuffleConfig customized = config.WithTier(2, new ShuffleTier(0.5, 1, 2));
            Assert.Equal(new ShuffleTier(0.5, 1, 2), customized.TierFor(2));
            ShuffleConfig kokushiOverride = config.WithTier(1, new ShuffleTier(0.4, 1, 1), ShuffleTarget.Kokushi);
            Assert.Equal(new ShuffleTier(0.4, 1, 1), kokushiOverride.TierFor(1, ShuffleTarget.Kokushi));

            // 异目标混合档：默认 (0.52,1,1)/(0.65,1,0)；服从全局上限；额度保留
            Assert.Equal(new ShuffleTier(0.52, 1, 1), config.MixedTierFor(2));
            Assert.Equal(new ShuffleTier(0.65, 1, 0), config.MixedTierFor(3));
            Assert.Null(config.MixedTierFor(4));
            ShuffleConfig low = new ShuffleConfig(enabled: true, strength: 0.2)
                .WithMixedTier(2, new ShuffleTier(1.0, 10, 10))
                .WithMixedTier(3, new ShuffleTier(0.9, 10, 10));
            Assert.Equal(0.2, low.MixedTierFor(2)!.Value.Strength, 9);
            Assert.Equal(0.2, low.MixedTierFor(3)!.Value.Strength, 9);
            Assert.Equal(10, low.MixedTierFor(2)!.Value.InitialBudget);
            Assert.Equal(10, low.MixedTierFor(3)!.Value.DrawBudget);

            // 异目标额度损耗：默认 0（含按人数回退）
            Assert.Equal(0.0, config.DiversityBudgetPenaltyFor(2), 9);
            Assert.Equal(0.0, config.DiversityBudgetPenaltyFor(3), 9);
            Assert.Equal(2.5, config.DiversityInterference, 9);
            Assert.Equal(0.0, config.DiversityBudgetPenalty, 9);

            // 只读视图防御性复制：每次访问返回新实例且内容与标定表一致
            Assert.NotSame(config.Tiers, config.Tiers);
            Assert.Equal(
                new Dictionary<int, ShuffleTier>
                {
                    { 1, new ShuffleTier(0.52, 1, 4) },
                    { 2, new ShuffleTier(0.52, 2, 2) },
                    { 3, new ShuffleTier(0.65, 3, 5) }
                },
                config.Tiers);
        }

        [Fact]
        public void ConfigAndTierRejectIllegalParameters()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleTier(1.2, 3, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleTier(double.NaN, 3, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleTier(0.5, -1, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleTier(0.5, 3, -1));

            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleConfig(enabled: true, selectionMillis: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleConfig(enabled: true, plannedDraws: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleConfig(enabled: true, plannedDraws: 19));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleConfig(enabled: true, strength: 1.2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleConfig(enabled: true, strength: double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleConfig(enabled: true, interference: -0.1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleConfig(enabled: true, interference: double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleConfig(enabled: true, diversityInterference: -1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleConfig(enabled: true, diversityBudgetPenalty: -0.1));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ShuffleConfig(enabled: true).WithTiers(new Dictionary<int, ShuffleTier>
                {
                    { 4, new ShuffleTier(0.5, 1, 1) }
                }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleConfig(enabled: true).WithTier(4, new ShuffleTier(0.5, 1, 1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleConfig(enabled: true).WithMixedTier(4, new ShuffleTier(0.5, 1, 1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShuffleConfig(enabled: true).WithDiversityBudgetPenalty(2, -0.1));
        }

        // ------------------------------------------------------------------
        // 看破抵消解析（V4 ShufflePreparationHandler.kt 冻结规则的手工展开：
        // 被抵消 ⇔ K 中存在“其他席位”；被抵消 → target=null 且 seeThrough=false；
        // 未被抵消 → 仅清除 seeThrough；费用按原始方案）
        // ------------------------------------------------------------------

        [Fact]
        public void SeeThroughResolverSuppressionMatchesFrozenV4Rule()
        {
            ShuffleTarget man = ShuffleTarget.ChinitsuMan;
            ShuffleTarget sou = ShuffleTarget.ChinitsuSou;
            ShuffleTarget pin = ShuffleTarget.ChinitsuPin;
            ShuffleTarget kokushi = ShuffleTarget.Kokushi;

            // 无人看破：有效方案 == 原始方案
            ShuffleSeeThroughResolution none = ShuffleSeeThroughResolver.Resolve(
                new ShuffleSelection?[] { new(man), new(sou), new(pin), new(kokushi) }, true);
            Assert.Empty(none.FrozenSeeThroughSeats);
            Assert.Equal(4, none.EffectiveCasterCount);
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(none.OriginalPlans[seat].Selection.Target, none.EffectivePlans[seat].Selection.Target);
                Assert.False(none.EffectivePlans[seat].Selection.SeeThrough);
            }

            // 单人看破（席位 0）：其余三席整效抵消，席位 0 自己保留目标、清除标记
            ShuffleSeeThroughResolution single = ShuffleSeeThroughResolver.Resolve(
                new ShuffleSelection?[] { new(man, true), new(sou), new(pin), new(kokushi) }, true);
            Assert.Equal(new[] { 0 }, single.FrozenSeeThroughSeats);
            Assert.Null(single.EffectivePlans[1].Selection.Target);
            Assert.Null(single.EffectivePlans[2].Selection.Target);
            Assert.Null(single.EffectivePlans[3].Selection.Target);
            Assert.Equal(man, single.EffectivePlans[0].Selection.Target);
            Assert.All(single.EffectivePlans, plan => Assert.False(plan.Selection.SeeThrough));
            Assert.Equal(1, single.EffectiveCasterCount);

            // 原始方案保持完整供费用结算：席位 0 目标+看破 = 40，四席合计 130
            Assert.Equal(40, single.OriginalPlans[0].Selection.Cost());
            Assert.Equal(30, single.OriginalPlans[1].Selection.Cost());
            Assert.Equal(130, single.OriginalPlans.Sum(plan => plan.Selection.Cost()));

            // 双人看破（席位 0、1）：K={0,1}，任何席位都存在“其他看破席位”→ 全员抵消
            ShuffleSeeThroughResolution doubleCast = ShuffleSeeThroughResolver.Resolve(
                new ShuffleSelection?[] { new(man, true), new(sou, true), new(pin), new(kokushi) }, true);
            Assert.Equal(new[] { 0, 1 }, doubleCast.FrozenSeeThroughSeats);
            Assert.Equal(0, doubleCast.EffectiveCasterCount);
            Assert.All(doubleCast.EffectivePlans, plan => Assert.Null(plan.Selection.Target));

            // 看破未启用：即使四席全点看破也不产生抵消，标记仍被清除
            ShuffleSeeThroughResolution disabled = ShuffleSeeThroughResolver.Resolve(
                new ShuffleSelection?[] { new(man, true), new(sou, true), new(pin, true), new(kokushi, true) }, false);
            Assert.Empty(disabled.FrozenSeeThroughSeats);
            Assert.Equal(4, disabled.EffectiveCasterCount);

            // 仅看破（无目标）是合法非空方案：自己的空目标保留，他人被抵消
            ShuffleSeeThroughResolution flagOnly = ShuffleSeeThroughResolver.Resolve(
                new ShuffleSelection?[] { new(null, true), new(man), null, null }, true);
            Assert.Equal(new[] { 0 }, flagOnly.FrozenSeeThroughSeats);
            Assert.Null(flagOnly.EffectivePlans[1].Selection.Target);
            Assert.Equal(0, flagOnly.EffectiveCasterCount);

            // 输入必须恰为四席
            Assert.Throws<ArgumentException>(() => ShuffleSeeThroughResolver.Resolve(
                new ShuffleSelection?[] { new(man) }, true));
            Assert.Throws<ArgumentException>(() => ShuffleSeeThroughResolver.Resolve(
                new ShuffleSelection?[] { new(man), new(sou), new(pin), new(kokushi), new(man) }, true));

            // null 选择视为跳过（V4 getOrNull ?: ShuffleSelection()）
            ShuffleSeeThroughResolution withNull = ShuffleSeeThroughResolver.Resolve(
                new ShuffleSelection?[] { null, new(man), null, null }, true);
            Assert.Equal(man, withNull.EffectivePlans[1].Selection.Target);
            Assert.Equal(0, withNull.OriginalPlans[0].Selection.Cost());
        }

        // ------------------------------------------------------------------
        // 随机记录校验（重复/缺失牌、越界随机值 → 零副作用拒绝）
        // ------------------------------------------------------------------

        private static TileId[] FullWall() => Enumerable.Range(0, 136).Select(v => new TileId(v)).ToArray();

        [Fact]
        public void TraceCreationRejectsDuplicateMissingAndOutOfRangeRecords()
        {
            double[] emptyDoubles = { 0.25, 0.75 };

            // 缺失牌：只有 135 张
            var missing = FullWall().Take(135).ToArray();
            bool okMissing = ShuffleRandomTrace.TryCreate("r1", missing, emptyDoubles, out _, out string? missingError);
            Assert.False(okMissing);
            Assert.Contains("exactly 136", missingError);

            // 重复牌：槽 0 与槽 1 同一张
            var duplicated = FullWall();
            duplicated[1] = duplicated[0];
            bool okDup = ShuffleRandomTrace.TryCreate("r2", duplicated, emptyDoubles, out _, out string? dupError);
            Assert.False(okDup);
            Assert.Contains("duplicate", dupError);

            // 随机值越界：1.0、负数、NaN、正无穷全部拒绝
            Assert.False(ShuffleRandomTrace.TryCreate("r3", FullWall(), new double[] { 1.0 }, out _, out string? tooLarge));
            Assert.Contains("[0,1)", tooLarge);
            Assert.False(ShuffleRandomTrace.TryCreate("r4", FullWall(), new double[] { -0.001 }, out _, out string? negative));
            Assert.Contains("[0,1)", negative);
            Assert.False(ShuffleRandomTrace.TryCreate("r5", FullWall(), new double[] { double.NaN }, out _, out _));
            Assert.False(ShuffleRandomTrace.TryCreate("r6", FullWall(), new double[] { double.PositiveInfinity }, out _, out _));

            // 空记录 ID 与 null 输入
            Assert.False(ShuffleRandomTrace.TryCreate("", FullWall(), emptyDoubles, out _, out string? emptyId));
            Assert.Contains("recordId", emptyId);
            Assert.False(ShuffleRandomTrace.TryCreate("r7", null!, emptyDoubles, out _, out _));
            Assert.False(ShuffleRandomTrace.TryCreate("r8", FullWall(), null!, out _, out _));

            // Create（抛出式）与 TryCreate 一致
            Assert.Throws<ArgumentException>(() => ShuffleRandomTrace.Create("r9", duplicated, emptyDoubles));

            // 合法记录：Create 成功、BaseWall 逐张保留、Doubles 保留
            ShuffleRandomTrace trace = ShuffleRandomTrace.Create("r10", FullWall(), emptyDoubles);
            Assert.Equal("r10", trace.RecordId);
            Assert.Equal(FullWall(), trace.BaseWall.WallOrder.ToList());
            Assert.Equal(emptyDoubles, trace.Doubles.ToList());

            ShuffleRandomTrace noDraws = ShuffleRandomTrace.WithoutDraws(trace.BaseWall);
            Assert.Empty(noDraws.Doubles);
            Assert.Equal("r10", noDraws.RecordId);
        }

        // ------------------------------------------------------------------
        // 回归（复核缺陷 #2）：记录创建后不可变——外部源数组与公开属性均不得篡改轨迹
        // ------------------------------------------------------------------

        [Fact]
        public void TraceRecordIsImmutableAfterCreation()
        {
            double[] source = { 0.25, 0.5, 0.75 };
            TileId[] wallSource = FullWall();
            ShuffleRandomTrace trace = ShuffleRandomTrace.Create("immutable", wallSource, source);

            // 外部修改创建时的源数组不得影响记录（输入防御性复制）
            source[0] = 0.99;
            wallSource[0] = new TileId(135);
            Assert.Equal(0.25, trace.Doubles[0]);
            Assert.Equal(new TileId(0), trace.BaseWall.WallOrder[0]);
            Assert.Equal(FullWall(), trace.BaseWall.WallOrder.ToList());

            // 公开属性不得转换为可变数组改写记录：
            // 若实现为只读包装，转换必须失败；若实现返回防御性副本，
            // 改写副本同样不得泄漏进记录（两种合规实现都通过）。
            if (trace.Doubles is double[] doublesView)
            {
                doublesView[1] = 0.123456789;
                Assert.NotEqual(0.123456789, trace.Doubles[1]);
            }

            if (trace.BaseWall.WallOrder is TileId[] wallView)
            {
                wallView[1] = new TileId(134);
                Assert.Equal(new TileId(1), trace.BaseWall.WallOrder[1]);
            }

            // 篡改尝试后记录仍完整：重复回放输入与创建时逐值一致
            Assert.Equal(new[] { 0.25, 0.5, 0.75 }, trace.Doubles.ToList());
        }
    }
}
