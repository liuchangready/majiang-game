using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Domain;
using Mahjong.Domain.Stacking;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// P1.5 牌山合成与回放验收。
    ///
    /// 期望值来源（绝不用被测实现作为 oracle）：
    /// - 基础洗牌由测试侧独立构造（System.Random 只允许出现在测试装配中，Domain 禁止创建 RNG）；
    /// - 守恒律（136 张唯一、3 张赤五）、死山 122..135 隔离、四人恒等、无目标/禁用恒等、
    ///   槽位-发牌一致性均为 V4 ShuffleAlgorithmAcceptanceTest 的冻结法律；
    /// - 回放一致性以“同一记录输入必须逐张复现”为契约断言；
    /// - 随机记录由探测-截断协议构造：先以充足候选值池探测精确消耗量，再截取为合法记录，
    ///   记录构造不依赖实现的牌序输出，仅依赖其消耗计数（记录格式的一部分）。
    /// </summary>
    public class ShuffleWallSynthesisTests
    {
        private const string RecordId = "p15-test-record";

        // ------------------------------------------------------------------
        // 测试侧独立随机装置（Domain 内禁止创建 RNG；此处为测试装配）
        // ------------------------------------------------------------------

        private static TileId[] RandomWall(int seed)
        {
            var tiles = new TileId[TileId.Count];
            for (int i = 0; i < TileId.Count; i++)
            {
                tiles[i] = new TileId(i);
            }

            var rng = new Random(seed);
            for (int i = TileId.Count - 1; i >= 1; i--)
            {
                int j = rng.Next(i + 1);
                (tiles[i], tiles[j]) = (tiles[j], tiles[i]);
            }

            return tiles;
        }

        private static double[] DoublePool(int seed, int count)
        {
            var rng = new Random(seed);
            var pool = new double[count];
            for (int i = 0; i < count; i++)
            {
                pool[i] = rng.NextDouble();
            }

            return pool;
        }

        private static readonly ShuffleConfig EnabledConfig = new ShuffleConfig(enabled: true);

        private static List<SeatShufflePlan> Plans(params (int Seat, ShuffleTarget Target)[] assignments)
        {
            return assignments.Select(a => new SeatShufflePlan(a.Seat, new ShuffleSelection(a.Target))).ToList();
        }

        /// <summary>探测-截断：用充足候选池确定精确消耗量，再构造恰好用尽的合法记录。</summary>
        private static ShuffleRandomTrace MakeTrace(
            int dealer,
            IReadOnlyList<SeatShufflePlan> plans,
            ShuffleConfig config,
            int wallSeed,
            int doubleSeed)
        {
            TileId[] wall = RandomWall(wallSeed);
            double[] pool = DoublePool(doubleSeed, 2048);

            ShuffleSynthesisResult probe = ShuffleWallSynthesizer.Shape(
                RecordId + "-probe", wall, pool, dealer, plans, config);

            Assert.NotEqual(ShuffleSynthesisErrorCode.TraceExhausted, probe.Error?.Code ?? ShuffleSynthesisErrorCode.None);
            Assert.True(probe.ConsumedRandomCount < pool.Length, "candidate pool must strictly exceed consumption");
            if (probe.IsSuccess)
            {
                Assert.Equal(0, probe.ConsumedRandomCount);
            }
            else
            {
                Assert.Equal(ShuffleSynthesisErrorCode.TraceNotFullyConsumed, probe.Error!.Code);
            }

            double[] exact = pool.Take(probe.ConsumedRandomCount).ToArray();
            return ShuffleRandomTrace.Create(RecordId + "-" + wallSeed + "-" + doubleSeed, wall, exact);
        }

        // ------------------------------------------------------------------
        // 零千术不变律（V4 `no target or disabled config equals the base shuffle exactly`、
        // `zero success budgets cannot alter the wall` 移植）
        // ------------------------------------------------------------------

        [Fact]
        public void NoTargetDisabledZeroStrengthAndZeroBudgetAllPreserveBaseWallExactly()
        {
            for (int seed = 0; seed < 32; seed++)
            {
                int dealer = seed % 4;
                TileId[] baseWall = RandomWall(seed);
                var nullPlans = Plans(
                    (0, ShuffleTarget.Kokushi), (1, ShuffleTarget.Kokushi), (2, ShuffleTarget.Kokushi), (3, ShuffleTarget.Kokushi));
                nullPlans = nullPlans.ConvertAll(p => new SeatShufflePlan(p.Seat, new ShuffleSelection()));

                // 全员 target==null（含 4 席）→ 基础洗牌逐张相等，消耗 0 个记录值；
                // 该分支同样严格校验轨迹：合法记录必须为空，多余值被拒绝
                ShuffleSynthesisResult nullTargets = ShuffleWallSynthesizer.Shape(
                    RecordId, baseWall, Array.Empty<double>(), dealer, nullPlans, EnabledConfig);
                Assert.True(nullTargets.IsSuccess);
                Assert.Equal(baseWall.ToList(), nullTargets.Wall!.WallOrder.ToList());
                Assert.Equal(0, nullTargets.ConsumedRandomCount);
                Assert.Equal(0, nullTargets.ActivePlanCount);
                ShuffleSynthesisResult nullTargetsSurplus = ShuffleWallSynthesizer.Shape(
                    RecordId, baseWall, new double[] { 0.5 }, dealer, nullPlans, EnabledConfig);
                Assert.False(nullTargetsSurplus.IsSuccess);
                Assert.Equal(ShuffleSynthesisErrorCode.TraceNotFullyConsumed, nullTargetsSurplus.Error!.Code);
                Assert.Null(nullTargetsSurplus.Wall);
                Assert.Equal(0, nullTargetsSurplus.ConsumedRandomCount);

                // 空方案列表
                ShuffleSynthesisResult emptyPlans = ShuffleWallSynthesizer.Shape(
                    RecordId, baseWall, Array.Empty<double>(), dealer, new List<SeatShufflePlan>(), EnabledConfig);
                Assert.Equal(baseWall.ToList(), emptyPlans.Wall!.WallOrder.ToList());
                Assert.Equal(0, emptyPlans.ConsumedRandomCount);

                // config.Enabled==false：有真实目标也必须恒等，且不消耗任何记录值
                var activePlans = Plans((0, ShuffleTarget.ChinitsuMan));
                ShuffleSynthesisResult disabled = ShuffleWallSynthesizer.Shape(
                    RecordId, baseWall, Array.Empty<double>(), dealer, activePlans,
                    new ShuffleConfig(enabled: false));
                Assert.True(disabled.IsSuccess);
                Assert.Equal(baseWall.ToList(), disabled.Wall!.WallOrder.ToList());
                Assert.Equal(0, disabled.ConsumedRandomCount);

                // 显式 strength=0 必须压过默认档位表（V4 验收测试明确注释该回归）。
                // 注意：strength=0 仍执行席位/槽位洗牌（消耗记录值），只是绝不换牌。
                ShuffleRandomTrace zeroStrengthTrace = MakeTrace(
                    dealer, activePlans, new ShuffleConfig(enabled: true, strength: 0.0),
                    wallSeed: seed, doubleSeed: seed + 3000);
                ShuffleSynthesisResult zeroStrength = ShuffleWallSynthesizer.Shape(
                    zeroStrengthTrace, dealer, activePlans, new ShuffleConfig(enabled: true, strength: 0.0));
                Assert.True(zeroStrength.IsSuccess);
                Assert.Equal(baseWall.ToList(), zeroStrength.Wall!.WallOrder.ToList());
                Assert.Equal(zeroStrengthTrace.Doubles.Count, zeroStrength.ConsumedRandomCount);
                Assert.True(zeroStrength.ConsumedRandomCount > 0, "strength=0 must still consume shuffle decisions");

                // 零成功额度（strength=1.0, budgets=0/0）也不得改变牌山
                var zeroBudgetConfig = EnabledConfig.WithTier(1, new ShuffleTier(1.0, 0, 0));
                ShuffleRandomTrace zeroBudgetTrace = MakeTrace(
                    dealer, activePlans, zeroBudgetConfig, wallSeed: seed, doubleSeed: seed + 4000);
                ShuffleSynthesisResult zeroBudget = ShuffleWallSynthesizer.Shape(
                    zeroBudgetTrace, dealer, activePlans, zeroBudgetConfig);
                Assert.True(zeroBudget.IsSuccess);
                Assert.Equal(baseWall.ToList(), zeroBudget.Wall!.WallOrder.ToList());
            }
        }

        // ------------------------------------------------------------------
        // 四人恒等律（V4 `four effective casters are identical to the base shuffle
        // tile for tile` 移植：seeds 0..127，含 reversed 与 seeThrough 标记）
        // ------------------------------------------------------------------

        [Fact]
        public void FourEffectiveCastersReturnBaseWallTileForTile()
        {
            var fourTargets = Plans(
                (0, ShuffleTarget.ChinitsuMan),
                (1, ShuffleTarget.Kokushi),
                (2, ShuffleTarget.Daisuushii),
                (3, ShuffleTarget.Daisangen));

            // seeThrough 标记不进入合成器（V4 权威层在传入前已清除/抵消）
            var fourWithFlags = fourTargets
                .Select(p => new SeatShufflePlan(p.Seat, new ShuffleSelection(p.Selection.Target!.Value, true)))
                .ToList();

            for (int seed = 0; seed < 128; seed++)
            {
                int dealer = seed % 4;
                TileId[] baseWall = RandomWall(seed);
                double[] doubles = Array.Empty<double>();

                ShuffleSynthesisResult forward = ShuffleWallSynthesizer.Shape(
                    RecordId, baseWall, doubles, dealer, fourTargets, EnabledConfig);
                Assert.True(forward.IsSuccess);
                Assert.Equal(4, forward.ActivePlanCount);
                Assert.Equal(0, forward.ConsumedRandomCount);
                Assert.Equal(baseWall.ToList(), forward.Wall!.WallOrder.ToList());

                ShuffleSynthesisResult reversed = ShuffleWallSynthesizer.Shape(
                    RecordId, baseWall, doubles, dealer, fourTargets.AsEnumerable().Reverse().ToList(), EnabledConfig);
                Assert.Equal(baseWall.ToList(), reversed.Wall!.WallOrder.ToList());

                ShuffleSynthesisResult flagged = ShuffleWallSynthesizer.Shape(
                    RecordId, baseWall, doubles, dealer, fourWithFlags, EnabledConfig);
                Assert.Equal(baseWall.ToList(), flagged.Wall!.WallOrder.ToList());
            }
        }

        // ------------------------------------------------------------------
        // 回归（复核缺陷 #1）：两条提前成功分支（无有效施术 / 四人有效施术）
        // 同样执行“轨迹严格恰好用尽”契约——多余记录值必须零副作用拒绝。
        // ------------------------------------------------------------------

        [Fact]
        public void IdentityPathsRejectSurplusTraceWithZeroSideEffects()
        {
            var noTargetPlans = Plans(
                (0, ShuffleTarget.Kokushi), (1, ShuffleTarget.Daisangen),
                (2, ShuffleTarget.ChinitsuMan), (3, ShuffleTarget.Daisuushii))
                .ConvertAll(p => new SeatShufflePlan(p.Seat, new ShuffleSelection()));
            var fourActivePlans = Plans(
                (0, ShuffleTarget.Kokushi), (1, ShuffleTarget.Daisangen),
                (2, ShuffleTarget.ChinitsuMan), (3, ShuffleTarget.Daisuushii));

            // 逐个单独值与多值 surplus 都必须被拒绝（0 值合法记录在上方恒等测试覆盖）
            double[][] surplusInputs =
            {
                new double[] { 0.5 },
                new double[] { 0.25, 0.75 },
                DoublePool(41, 64)
            };

            for (int dealer = 0; dealer <= 3; dealer++)
            {
                TileId[] baseWall = RandomWall(dealer);

                // 分支 A：无人做牌（全员 target==null）与禁用配置（active.Count==0）
                IReadOnlyList<SeatShufflePlan>[] branchAPlans =
                {
                    noTargetPlans,
                    Plans((0, ShuffleTarget.ChinitsuMan))
                };
                ShuffleConfig[] branchAConfigs = { EnabledConfig, new ShuffleConfig(enabled: false) };
                for (int branch = 0; branch < branchAPlans.Length; branch++)
                {
                    IReadOnlyList<SeatShufflePlan> plans = branchAPlans[branch];
                    ShuffleConfig config = branchAConfigs[branch];

                    foreach (double[] surplus in surplusInputs)
                    {
                        ShuffleSynthesisResult rejected = ShuffleWallSynthesizer.Shape(
                            RecordId, baseWall, surplus, dealer, plans, config);
                        Assert.False(rejected.IsSuccess);
                        Assert.Equal(ShuffleSynthesisErrorCode.TraceNotFullyConsumed, rejected.Error!.Code);
                        Assert.Contains("consumed 0 of", rejected.Error.Message);
                        Assert.Null(rejected.Wall);
                        Assert.Equal(0, rejected.ConsumedRandomCount);
                        Assert.Equal(0, rejected.ActivePlanCount);
                    }

                    // 同输入的合法空记录必须成功（证明拒绝只源于多余值）
                    ShuffleSynthesisResult accepted = ShuffleWallSynthesizer.Shape(
                        RecordId, baseWall, Array.Empty<double>(), dealer, plans, config);
                    Assert.True(accepted.IsSuccess);
                    Assert.Equal(baseWall.ToList(), accepted.Wall!.WallOrder.ToList());
                    Assert.Equal(0, accepted.ConsumedRandomCount);
                }

                // 分支 B：四人有效施术（n==4 硬性恒等）
                foreach (double[] surplus in surplusInputs)
                {
                    ShuffleSynthesisResult rejected = ShuffleWallSynthesizer.Shape(
                        RecordId, baseWall, surplus, dealer, fourActivePlans, EnabledConfig);
                    Assert.False(rejected.IsSuccess);
                    Assert.Equal(ShuffleSynthesisErrorCode.TraceNotFullyConsumed, rejected.Error!.Code);
                    Assert.Contains("consumed 0 of", rejected.Error.Message);
                    Assert.Null(rejected.Wall);
                    Assert.Equal(0, rejected.ConsumedRandomCount);
                    Assert.Equal(4, rejected.ActivePlanCount);
                }

                ShuffleSynthesisResult acceptedFour = ShuffleWallSynthesizer.Shape(
                    RecordId, baseWall, Array.Empty<double>(), dealer, fourActivePlans, EnabledConfig);
                Assert.True(acceptedFour.IsSuccess);
                Assert.Equal(baseWall.ToList(), acceptedFour.Wall!.WallOrder.ToList());
                Assert.Equal(4, acceptedFour.ActivePlanCount);
                Assert.Equal(0, acceptedFour.ConsumedRandomCount);
            }
        }

        // ------------------------------------------------------------------
        // 三人做牌：136 守恒、赤五恒 3、死山隔离、排序无关（V4
        // `generation conserves identities dead wall and is reproducible...` 移植）
        // ------------------------------------------------------------------

        [Fact]
        public void ThreeCastersShapeWallPreservingConservationDeadWallAndOrderIndependence()
        {
            var three = Plans(
                (0, ShuffleTarget.Kokushi),
                (1, ShuffleTarget.Daisangen),
                (2, ShuffleTarget.ChinitsuMan));

            int changedCount = 0;
            for (int seed = 0; seed < 32; seed++)
            {
                int dealer = seed % 4;
                TileId[] baseWall = RandomWall(seed);
                ShuffleRandomTrace trace = MakeTrace(dealer, three, EnabledConfig, wallSeed: seed, doubleSeed: seed + 9000);

                ShuffleSynthesisResult result = ShuffleWallSynthesizer.Shape(trace, dealer, three, EnabledConfig);
                Assert.True(result.IsSuccess);
                Assert.Equal(trace.Doubles.Count, result.ConsumedRandomCount);
                Assert.Equal(3, result.ActivePlanCount);

                IReadOnlyList<TileId> shaped = result.Wall!.WallOrder;

                // 136 张唯一且与标准全集一致
                Assert.Equal(136, shaped.Count);
                Assert.Equal(136, shaped.Select(t => t.Value).Distinct().Count());
                Assert.Equal(TileId.All.ToList(), shaped.OrderBy(t => t.Value).ToList());

                // 恰好 3 张赤五（万/索/饼 5 的 copyIndex==3）
                Assert.Equal(3, shaped.Count(t => t.IsRedFive));

                // 死山 122..135 与基础洗牌逐张相等
                for (int slot = ShuffleSlots.DeadWallStart; slot <= ShuffleSlots.DeadWallEnd; slot++)
                {
                    Assert.Equal(baseWall[slot], shaped[slot]);
                }

                // 排序无关性：reversed 输入 + 同一记录 → 逐张一致
                ShuffleSynthesisResult reversed = ShuffleWallSynthesizer.Shape(
                    trace, dealer, three.AsEnumerable().Reverse().ToList(), EnabledConfig);
                Assert.True(reversed.IsSuccess);
                Assert.Equal(shaped.ToList(), reversed.Wall!.WallOrder.ToList());

                if (!shaped.SequenceEqual(baseWall))
                {
                    changedCount++;
                }
            }

            Assert.True(changedCount > 0, "n=3 must still perform exchanges");
        }

        // ------------------------------------------------------------------
        // 起手锁定与死山隔离（V4 `draw phase never steals the dealt initial wall` 移植）
        // ------------------------------------------------------------------

        [Fact]
        public void DrawPhaseNeverTouchesInitialSlotsOrDeadWall()
        {
            var config = EnabledConfig.WithTier(1, new ShuffleTier(0.75, 0, 4));
            var plans = Plans((2, ShuffleTarget.Daisangen));

            for (int seed = 100; seed < 132; seed++)
            {
                int dealer = 3;
                TileId[] baseWall = RandomWall(seed);
                ShuffleRandomTrace trace = MakeTrace(dealer, plans, config, wallSeed: seed, doubleSeed: seed + 11000);

                ShuffleSynthesisResult shaped = ShuffleWallSynthesizer.Shape(trace, dealer, plans, config);
                Assert.True(shaped.IsSuccess);
                IReadOnlyList<TileId> wall = shaped.Wall!.WallOrder;

                for (int slot = 0; slot < ShuffleSlots.InitialDealTiles; slot++)
                {
                    Assert.True(baseWall[slot].Equals(wall[slot]), "draw phase must not touch 0..52");
                }

                for (int slot = ShuffleSlots.DeadWallStart; slot <= ShuffleSlots.DeadWallEnd; slot++)
                {
                    Assert.True(baseWall[slot].Equals(wall[slot]), "dead wall must never change");
                }
            }
        }

        // ------------------------------------------------------------------
        // 回放一致性与轨迹纪律：同一记录逐张复现；不足/多余轨迹被拒绝
        // ------------------------------------------------------------------

        [Fact]
        public void ReplayWithSameRecordedInputIsTileForTileIdentical()
        {
            var three = Plans(
                (0, ShuffleTarget.Kokushi),
                (1, ShuffleTarget.Daisangen),
                (2, ShuffleTarget.ChinitsuMan));

            int differingInputs = 0;
            for (int seed = 0; seed < 16; seed++)
            {
                int dealer = seed % 4;
                TileId[] baseWall = RandomWall(seed);
                ShuffleRandomTrace trace = MakeTrace(dealer, three, EnabledConfig, wallSeed: seed, doubleSeed: seed + 13000);

                ShuffleSynthesisResult first = ShuffleWallSynthesizer.Shape(trace, dealer, three, EnabledConfig);
                Assert.True(first.IsSuccess);

                // 记录入口（持久化形态）与原始入口（recordId + 原始列表）必须逐张一致
                ShuffleSynthesisResult byTrace = ShuffleWallSynthesizer.Shape(trace, dealer, three, EnabledConfig);
                ShuffleSynthesisResult byRaw = ShuffleWallSynthesizer.Shape(
                    trace.RecordId, trace.BaseWall.WallOrder, trace.Doubles, dealer, three, EnabledConfig);
                Assert.Equal(first.Wall!.WallOrder.ToList(), byTrace.Wall!.WallOrder.ToList());
                Assert.Equal(first.Wall!.WallOrder.ToList(), byRaw.Wall!.WallOrder.ToList());
                Assert.Equal(first.ConsumedRandomCount, byRaw.ConsumedRandomCount);

                // 不同的记录输入（不同随机值池）必须驱动不同结果（输入真正参与决策）
                ShuffleRandomTrace otherTrace = MakeTrace(dealer, three, EnabledConfig, wallSeed: seed, doubleSeed: seed + 14000);
                ShuffleSynthesisResult withOtherTrace = ShuffleWallSynthesizer.Shape(otherTrace, dealer, three, EnabledConfig);
                Assert.True(withOtherTrace.IsSuccess);
                if (!withOtherTrace.Wall!.WallOrder.SequenceEqual(first.Wall!.WallOrder))
                {
                    differingInputs++;
                }

                // 轨迹不足：截掉最后一个记录值 → TraceExhausted 零副作用失败
                var truncated = trace.Doubles.Take(trace.Doubles.Count - 1).ToArray();
                ShuffleSynthesisResult exhausted = ShuffleWallSynthesizer.Shape(
                    trace.RecordId, trace.BaseWall.WallOrder, truncated, dealer, three, EnabledConfig);
                Assert.False(exhausted.IsSuccess);
                Assert.Equal(ShuffleSynthesisErrorCode.TraceExhausted, exhausted.Error!.Code);
                Assert.Null(exhausted.Wall);

                // 轨迹多余：追加一个未消耗值 → TraceNotFullyConsumed
                var surplus = trace.Doubles.Concat(new double[] { 0.123456 }).ToArray();
                ShuffleSynthesisResult notConsumed = ShuffleWallSynthesizer.Shape(
                    trace.RecordId, trace.BaseWall.WallOrder, surplus, dealer, three, EnabledConfig);
                Assert.False(notConsumed.IsSuccess);
                Assert.Equal(ShuffleSynthesisErrorCode.TraceNotFullyConsumed, notConsumed.Error!.Code);
                Assert.Null(notConsumed.Wall);
            }

            Assert.True(differingInputs > 0, "different recorded traces must be able to drive different walls");
        }

        // ------------------------------------------------------------------
        // 记录墙 → TilePositionMap 集成：槽位-发牌布局一致、状态哈希一致、抽牌序列记录回放
        // ------------------------------------------------------------------

        [Fact]
        public void RecordedWallFeedsTilePositionMapAndDealsMatchSlotLayout()
        {
            var three = Plans(
                (0, ShuffleTarget.Kokushi),
                (1, ShuffleTarget.Daisangen),
                (2, ShuffleTarget.ChinitsuMan));

            for (int seed = 0; seed < 8; seed++)
            {
                int dealer = seed % 4;
                ShuffleRandomTrace trace = MakeTrace(dealer, three, EnabledConfig, wallSeed: seed, doubleSeed: seed + 15000);
                ShuffleSynthesisResult result = ShuffleWallSynthesizer.Shape(trace, dealer, three, EnabledConfig);
                Assert.True(result.IsSuccess);
                RecordedRandomDraw wall = result.Wall!;

                // CreateWall + DealInitial 与 CreateDealt 收敛到同一状态哈希
                TilePositionMap viaDeal = TilePositionMap.CreateWall(wall).DealInitial(dealer).RequireState();
                TilePositionMap viaDealt = TilePositionMap.CreateDealt(wall, dealer);
                Assert.Equal(viaDeal.StateHash, viaDealt.StateHash);
                Assert.True(TilePositionMap.Validate(viaDealt).IsValid);

                // 每席手牌恰为本人槽位上的牌（槽位计算与实体发牌互相印证）
                IReadOnlyList<TileId> shapedOrder = wall.WallOrder;
                for (int seat = 0; seat <= 3; seat++)
                {
                    IReadOnlyList<int> slots = ShuffleSlots.Initial(dealer, seat);
                    var handTiles = new HashSet<int>();
                    for (int v = 0; v < TileId.Count; v++)
                    {
                        TilePosition position = viaDealt[new TileId(v)];
                        if (position.Zone == TileZone.Hand && position.Seat == seat)
                        {
                            handTiles.Add(v);
                        }
                    }

                    Assert.Equal(
                        slots.Select(s => shapedOrder[s].Value).OrderBy(x => x).ToList(),
                        handTiles.OrderBy(x => x).ToList());
                }

                // 抽牌序列作为记录追加，回放收敛到同一状态
                int nextSeat = (dealer + 1) % 4;
                TransitionResult draw = viaDealt.DrawFromWall(nextSeat);
                Assert.True(draw.IsSuccess, draw.Error?.ToString());
                RecordedRandomDraw withDraw = wall.AppendDraw(nextSeat, draw.ResultTile!.Value, DrawSource.Wall);
                Assert.Single(withDraw.Draws);

                TilePositionMap replayed = TilePositionMap.CreateWall(withDraw)
                    .DealInitial(dealer).RequireState().DrawFromWall(nextSeat).RequireState();
                TilePositionMap direct = TilePositionMap.CreateDealt(wall, dealer)
                    .DrawFromWall(nextSeat).RequireState();
                Assert.Equal(direct.StateHash, replayed.StateHash);
            }
        }

        // ------------------------------------------------------------------
        // 非法输入零副作用拒绝（合成器边界错误码全覆盖）
        // ------------------------------------------------------------------

        [Fact]
        public void IllegalInputsAreRejectedWithZeroSideEffects()
        {
            TileId[] baseWall = RandomWall(7);
            var plans = Plans((0, ShuffleTarget.ChinitsuMan));
            double[] doubles = { 0.5 };

            void AssertFailure(ShuffleSynthesisResult result, ShuffleSynthesisErrorCode code)
            {
                Assert.False(result.IsSuccess);
                Assert.Equal(code, result.Error!.Code);
                Assert.Null(result.Wall);
            }

            // recordId
            AssertFailure(
                ShuffleWallSynthesizer.Shape("", baseWall, doubles, 0, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidRecordId);

            // 基础牌序：重复 / 缺失 / null
            var duplicated = (TileId[])baseWall.Clone();
            duplicated[5] = duplicated[3];
            AssertFailure(
                ShuffleWallSynthesizer.Shape(RecordId, duplicated, doubles, 0, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidBaseWall);
            AssertFailure(
                ShuffleWallSynthesizer.Shape(RecordId, baseWall.Take(135).ToArray(), doubles, 0, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidBaseWall);
            AssertFailure(
                ShuffleWallSynthesizer.Shape(RecordId, null!, doubles, 0, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidBaseWall);

            // 记录随机值：NaN / 越界 / null
            AssertFailure(
                ShuffleWallSynthesizer.Shape(RecordId, baseWall, new double[] { double.NaN }, 0, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidTraceDouble);
            AssertFailure(
                ShuffleWallSynthesizer.Shape(RecordId, baseWall, new double[] { 1.0 }, 0, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidTraceDouble);

            // 庄家座位
            AssertFailure(
                ShuffleWallSynthesizer.Shape(RecordId, baseWall, doubles, -1, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidDealerSeat);
            AssertFailure(
                ShuffleWallSynthesizer.Shape(RecordId, baseWall, doubles, 4, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidDealerSeat);

            // 方案：null 元素 / 重复座位（座位越界在 SeatShufflePlan 构造边界即被拒绝）
            AssertFailure(
                ShuffleWallSynthesizer.Shape(RecordId, baseWall, doubles, 0, new List<SeatShufflePlan> { null! }, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidPlanSeat);
            Assert.Throws<ArgumentOutOfRangeException>(() => new SeatShufflePlan(4, new ShuffleSelection(ShuffleTarget.Kokushi)));
            AssertFailure(
                ShuffleWallSynthesizer.Shape(
                    RecordId, baseWall, doubles, 0,
                    Plans((1, ShuffleTarget.Kokushi), (1, ShuffleTarget.Daisangen)), EnabledConfig),
                ShuffleSynthesisErrorCode.DuplicatePlanSeat);
        }

        // ------------------------------------------------------------------
        // 目标增益（V4 `every target improves its intended initial hand...` 移植：
        // 统一测试档位 (0.75,3,4)，96 个固定样本，增益 > 48）
        // ------------------------------------------------------------------

        private static int Affinity(IReadOnlyList<TileId> tiles, ShuffleTarget target)
        {
            double[] demand = ShuffleTargets.Demand(target);
            if (target == ShuffleTarget.Kokushi)
            {
                return tiles.Where(t => demand[t.TypeIndex] > 0.0).Select(t => t.TypeIndex).Distinct().Count();
            }

            return tiles.Count(t => demand[t.TypeIndex] > 0.0);
        }

        [Fact]
        public void EveryTargetImprovesItsIntendedInitialHandOverFixedSample()
        {
            var allTargets = new[]
            {
                ShuffleTarget.Kokushi,
                ShuffleTarget.ChinitsuMan,
                ShuffleTarget.Daisangen,
                ShuffleTarget.Daisuushii,
                ShuffleTarget.ChinitsuSou,
                ShuffleTarget.ChinitsuPin
            };

            var testTier = new ShuffleTier(0.75, 3, 4);
            ShuffleConfig config = EnabledConfig.WithTier(1, testTier);
            foreach (ShuffleTarget target in allTargets)
            {
                config = config.WithTier(1, testTier, target);
            }

            foreach (ShuffleTarget target in allTargets)
            {
                int gain = 0;
                for (int seed = 0; seed < 96; seed++)
                {
                    int dealer = seed % 4;
                    int seat = (seed / 4) % 4;
                    IReadOnlyList<int> slots = ShuffleSlots.Initial(dealer, seat);

                    TileId[] baseWall = RandomWall(seed);
                    ShuffleRandomTrace baselineTrace = MakeTrace(dealer, new List<SeatShufflePlan>(), EnabledConfig, wallSeed: seed, doubleSeed: seed + 16000);
                    ShuffleRandomTrace shapedTrace = MakeTrace(dealer, Plans((seat, target)), config, wallSeed: seed, doubleSeed: seed + 17000);

                    ShuffleSynthesisResult baseline = ShuffleWallSynthesizer.Shape(baselineTrace, dealer, new List<SeatShufflePlan>(), EnabledConfig);
                    ShuffleSynthesisResult shaped = ShuffleWallSynthesizer.Shape(shapedTrace, dealer, Plans((seat, target)), config);
                    Assert.True(baseline.IsSuccess);
                    Assert.True(shaped.IsSuccess);

                    List<TileId> baselineHand = slots.Select(s => baseline.Wall!.WallOrder[s]).ToList();
                    List<TileId> shapedHand = slots.Select(s => shaped.Wall!.WallOrder[s]).ToList();
                    gain += Affinity(shapedHand, target) - Affinity(baselineHand, target);
                }

                Assert.True(gain > 48, "Expected measurable progress for " + target + ", total gain=" + gain);
            }
        }

        // ------------------------------------------------------------------
        // 尾段摸牌槽受益（V4 `late expected draw slots can receive gains` 移植）
        // ------------------------------------------------------------------

        [Fact]
        public void LateDrawSlotsGainAlongsideEarlySlots()
        {
            var config = EnabledConfig.WithTier(1, new ShuffleTier(0.75, 0, 4));
            int dealer = 1;
            int seat = 3;
            var plans = Plans((seat, ShuffleTarget.ChinitsuMan));
            IReadOnlyList<int> slots = ShuffleSlots.Draws(dealer, seat, 18);

            int early = 0;
            int late = 0;
            for (int seed = 0; seed < 128; seed++)
            {
                TileId[] baseWall = RandomWall(seed);
                ShuffleRandomTrace baselineTrace = MakeTrace(dealer, new List<SeatShufflePlan>(), EnabledConfig, wallSeed: seed, doubleSeed: seed + 18000);
                ShuffleRandomTrace shapedTrace = MakeTrace(dealer, plans, config, wallSeed: seed, doubleSeed: seed + 19000);

                ShuffleSynthesisResult baseline = ShuffleWallSynthesizer.Shape(baselineTrace, dealer, new List<SeatShufflePlan>(), EnabledConfig);
                ShuffleSynthesisResult shaped = ShuffleWallSynthesizer.Shape(shapedTrace, dealer, plans, config);
                Assert.True(baseline.IsSuccess);
                Assert.True(shaped.IsSuccess);

                IReadOnlyList<TileId> baselineWall = baseline.Wall!.WallOrder;
                IReadOnlyList<TileId> shapedWall = shaped.Wall!.WallOrder;
                early += Affinity(slots.Take(9).Select(s => shapedWall[s]).ToList(), ShuffleTarget.ChinitsuMan) -
                         Affinity(slots.Take(9).Select(s => baselineWall[s]).ToList(), ShuffleTarget.ChinitsuMan);
                late += Affinity(slots.Skip(9).Select(s => shapedWall[s]).ToList(), ShuffleTarget.ChinitsuMan) -
                        Affinity(slots.Skip(9).Select(s => baselineWall[s]).ToList(), ShuffleTarget.ChinitsuMan);
            }

            Assert.True(early > 0, "early slots should gain: " + early);
            Assert.True(late > 0, "late planned slots must also gain: " + late);
        }

        // ------------------------------------------------------------------
        // 同目标资源竞争（V4 `same-resource competition is weaker...` 移植）
        // ------------------------------------------------------------------

        [Fact]
        public void SameResourceCompetitionIsWeakerThanIndependentTargets()
        {
            var tier = new ShuffleTier(0.9, 10, 0);
            ShuffleConfig config = new ShuffleConfig(enabled: true, diversityInterference: 0.0)
                .WithTier(2, tier, ShuffleTarget.ChinitsuMan)
                .WithTier(2, tier, ShuffleTarget.ChinitsuPin)
                .WithMixedTier(2, tier);

            int same = 0;
            int independent = 0;
            for (int seed = 0; seed < 128; seed++)
            {
                int dealer = 0;
                IReadOnlyList<int> slots = ShuffleSlots.Initial(0, 0);
                var own = Plans((0, ShuffleTarget.ChinitsuMan));
                var sameTarget = own.Concat(Plans((1, ShuffleTarget.ChinitsuMan))).ToList();
                var mixedTarget = own.Concat(Plans((1, ShuffleTarget.ChinitsuPin))).ToList();

                ShuffleRandomTrace traceA = MakeTrace(dealer, sameTarget, config, wallSeed: seed, doubleSeed: seed + 20000);
                ShuffleRandomTrace traceB = MakeTrace(dealer, mixedTarget, config, wallSeed: seed, doubleSeed: seed + 21000);

                ShuffleSynthesisResult a = ShuffleWallSynthesizer.Shape(traceA, dealer, sameTarget, config);
                ShuffleSynthesisResult b = ShuffleWallSynthesizer.Shape(traceB, dealer, mixedTarget, config);
                Assert.True(a.IsSuccess);
                Assert.True(b.IsSuccess);

                same += Affinity(slots.Select(s => a.Wall!.WallOrder[s]).ToList(), ShuffleTarget.ChinitsuMan);
                independent += Affinity(slots.Select(s => b.Wall!.WallOrder[s]).ToList(), ShuffleTarget.ChinitsuMan);
            }

            Assert.True(independent > same, "Same-suit competition must have real effect: same=" + same + " independent=" + independent);
        }
    }
}
