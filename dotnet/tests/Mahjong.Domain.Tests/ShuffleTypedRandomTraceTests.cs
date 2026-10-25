using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Domain;
using Mahjong.Domain.Stacking;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// D-R1-02 typed 有序随机输入 seam 验收。
    ///
    /// 本类用真实公开入口 <see cref="ShuffleWallSynthesizer.ShapeWithTypedRandomTrace"/>，
    /// 覆盖记录创建、类型/边界校验、kind/bound 消费纪律、恒等/零额度分支、非恒等合成、
    /// 方案排列正控制、136/死山保护和逐张确定性。
    ///
    /// 重要口径：测试中的 typed 事件序列来自对同一 C# 合成器的 legacy double 执行请求记录，
    /// 只用于证明“同一算法下 typed 与 legacy 兼容”以及 typed seam 自身的行为；
    /// 它是 C# 自测，不是 V4 黄金样本，不证明 V4 跨运行时或逐墙一致。
    /// </summary>
    public class ShuffleTypedRandomTraceTests
    {
        private const string RecordId = "typed-seam-test";

        private static readonly ShuffleConfig EnabledConfig = new ShuffleConfig(enabled: true);

        // ------------------------------------------------------------------
        // 测试侧独立随机装置（Domain 内禁止创建 RNG；此处只装配输入）
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

        private static List<SeatShufflePlan> Plans(params (int Seat, ShuffleTarget Target)[] assignments)
        {
            return assignments.Select(a => new SeatShufflePlan(a.Seat, new ShuffleSelection(a.Target))).ToList();
        }

        private static List<SeatShufflePlan> ThreePlans()
        {
            return Plans(
                (0, ShuffleTarget.Kokushi),
                (1, ShuffleTarget.Daisangen),
                (2, ShuffleTarget.ChinitsuMan));
        }

        private static ShuffleTypedRandomEvent ToEvent(ShuffleTypedRandomDraw draw)
        {
            return draw.Kind == ShuffleTypedRandomDrawKind.NextInt
                ? ShuffleTypedRandomEvent.ForNextInt(draw.Bound, draw.Result)
                : ShuffleTypedRandomEvent.ForNextDouble(draw.Value);
        }

        private static List<ShuffleTypedRandomEvent> EventsOf(ShuffleTypedRandomTrace trace)
        {
            return trace.Draws.Select(ToEvent).ToList();
        }

        /// <summary>
        /// 用 legacy double 入口的真实执行请求记录构造 typed 输入：
        /// 先以充足候选池探测精确消耗量，再截取为恰好用尽的合法 double 记录；
        /// legacy 成功回放的 RandomRequests 给出本次执行实际使用的 kind/bound/value 顺序。
        /// 这不是 V4 事件提取，只是 typed/legacy 同算法兼容性自测。
        /// </summary>
        private static (ShuffleTypedRandomTrace Trace, RecordedRandomDraw LegacyWall) MakeTypedTrace(
            int dealer,
            IReadOnlyList<SeatShufflePlan> plans,
            ShuffleConfig config,
            int wallSeed,
            int doubleSeed)
        {
            TileId[] wall = RandomWall(wallSeed);
            double[] pool = DoublePool(doubleSeed, 4096);

            ShuffleSynthesisResult probe = ShuffleWallSynthesizer.Shape(
                RecordId + "-probe", wall, pool, dealer, plans, config);
            Assert.NotEqual(
                ShuffleSynthesisErrorCode.TraceExhausted,
                probe.Error?.Code ?? ShuffleSynthesisErrorCode.None);
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
            ShuffleSynthesisResult legacy = ShuffleWallSynthesizer.Shape(
                RecordId + "-legacy", wall, exact, dealer, plans, config);
            Assert.True(legacy.IsSuccess, legacy.Error?.ToString());
            Assert.Equal(exact.Length, legacy.ConsumedRandomCount);

            List<ShuffleTypedRandomEvent> events = legacy.RandomRequests.Select(ToEvent).ToList();
            ShuffleTypedRandomTrace trace = ShuffleTypedRandomTrace.Create(RecordId + "-typed", wall, events);
            return (trace, legacy.Wall!);
        }

        private static void AssertFailure(
            ShuffleSynthesisResult result,
            ShuffleSynthesisErrorCode code,
            int consumedRandomCount)
        {
            Assert.False(result.IsSuccess);
            Assert.Equal(code, result.Error!.Code);
            Assert.Null(result.Wall);
            Assert.Equal(consumedRandomCount, result.ConsumedRandomCount);
        }

        private static void AssertSameDraw(ShuffleTypedRandomDraw expected, ShuffleTypedRandomDraw actual)
        {
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.Bound, actual.Bound);
            Assert.Equal(expected.Result, actual.Result);
            Assert.Equal(expected.Value, actual.Value);
        }

        // ------------------------------------------------------------------
        // 记录创建：复制输入、只读视图、严格有序
        // ------------------------------------------------------------------

        [Fact]
        public void TraceCopiesInputsAndExposesOnlyImmutableOrderedDraws()
        {
            TileId[] baseWall = RandomWall(5);
            TileId firstTile = baseWall[0];
            var events = new List<ShuffleTypedRandomEvent>
            {
                ShuffleTypedRandomEvent.ForNextInt(6, 4),
                ShuffleTypedRandomEvent.ForNextDouble(0.25),
                ShuffleTypedRandomEvent.ForNextInt(1, 0)
            };

            ShuffleTypedRandomTrace trace = ShuffleTypedRandomTrace.Create(RecordId, baseWall, events);

            Assert.Equal(RecordId, trace.RecordId);
            Assert.Equal(3, trace.Count);
            Assert.Equal(3, trace.Draws.Count);
            Assert.Equal(ShuffleTypedRandomDrawKind.NextInt, trace.Draws[0].Kind);
            Assert.Equal(6, trace.Draws[0].Bound);
            Assert.Equal(4, trace.Draws[0].Result);
            Assert.Equal(0.0, trace.Draws[0].Value);
            Assert.Equal(ShuffleTypedRandomDrawKind.NextDouble, trace.Draws[1].Kind);
            Assert.Equal(0.25, trace.Draws[1].Value);
            Assert.Equal(0, trace.Draws[1].Bound);
            Assert.Equal(0, trace.Draws[1].Result);
            Assert.Equal(ShuffleTypedRandomDrawKind.NextInt, trace.Draws[2].Kind);
            Assert.Equal(1, trace.Draws[2].Bound);
            Assert.Equal(0, trace.Draws[2].Result);

            // 创建后修改源数组/事件列表不得影响记录。
            baseWall[0] = baseWall[1];
            events.Clear();
            Assert.Equal(firstTile, trace.BaseWall.WallOrder[0]);
            Assert.Equal(3, trace.Draws.Count);

            // Draws 是只读集合，不能强转为可变数组；IList.Add 必须失败。
            Assert.IsNotType<ShuffleTypedRandomDraw[]>(trace.Draws);
            var mutableView = Assert.IsAssignableFrom<IList<ShuffleTypedRandomDraw>>(trace.Draws);
            Assert.Throws<NotSupportedException>(() => mutableView.Add(trace.Draws[0]));

            // WithoutDraws 同样复制基础墙。
            TileId[] otherWall = RandomWall(6);
            RecordedRandomDraw baseDraw = RecordedRandomDraw.Create(RecordId, otherWall);
            ShuffleTypedRandomTrace empty = ShuffleTypedRandomTrace.WithoutDraws(baseDraw);
            otherWall[0] = otherWall[1];
            Assert.Equal(0, empty.Count);
            Assert.Equal(baseDraw.WallOrder.ToList(), empty.BaseWall.WallOrder.ToList());
        }

        // ------------------------------------------------------------------
        // 记录创建：非法 kind/bound/result/value/NaN/Infinity/基础墙全部拒绝
        // ------------------------------------------------------------------

        [Fact]
        public void TraceCreationRejectsInvalidKindsBoundsValuesAndBaseWalls()
        {
            TileId[] baseWall = RandomWall(7);
            var valid = new List<ShuffleTypedRandomEvent> { ShuffleTypedRandomEvent.ForNextDouble(0.5) };

            void Reject(
                string label,
                string recordId,
                IReadOnlyList<TileId>? wall,
                IReadOnlyList<ShuffleTypedRandomEvent>? events)
            {
                bool created = ShuffleTypedRandomTrace.TryCreate(
                    recordId, wall!, events!, out ShuffleTypedRandomTrace? trace, out string? error);
                Assert.False(created, label);
                Assert.Null(trace);
                Assert.False(string.IsNullOrEmpty(error), label + " must report a reason");
                Assert.Throws<ArgumentException>(() => ShuffleTypedRandomTrace.Create(recordId, wall!, events!));
            }

            Reject("null recordId", null!, baseWall, valid);
            Reject("empty recordId", "", baseWall, valid);
            Reject("null base wall", RecordId, null, valid);
            Reject("short base wall", RecordId, baseWall.Take(135).ToArray(), valid);
            Reject("extra base wall", RecordId, baseWall.Concat(new[] { new TileId(0) }).ToArray(), valid);
            Reject("empty base wall", RecordId, Array.Empty<TileId>(), valid);
            Reject("duplicate base wall", RecordId, Duplicate(baseWall), valid);
            Reject("null events", RecordId, baseWall, null);
            Reject("null event", RecordId, baseWall, new List<ShuffleTypedRandomEvent> { null! });
            Reject("kind zero", RecordId, baseWall, One(new ShuffleTypedRandomEvent(0, 2, 0, 0.0)));
            Reject("unknown kind", RecordId, baseWall, One(new ShuffleTypedRandomEvent((ShuffleTypedRandomDrawKind)99, 2, 0, 0.0)));
            Reject("zero bound", RecordId, baseWall, One(ShuffleTypedRandomEvent.ForNextInt(0, 0)));
            Reject("negative bound", RecordId, baseWall, One(ShuffleTypedRandomEvent.ForNextInt(-1, 0)));
            Reject("negative result", RecordId, baseWall, One(ShuffleTypedRandomEvent.ForNextInt(2, -1)));
            Reject("result equals bound", RecordId, baseWall, One(ShuffleTypedRandomEvent.ForNextInt(2, 2)));
            Reject("result above bound", RecordId, baseWall, One(ShuffleTypedRandomEvent.ForNextInt(2, 3)));
            Reject("nextInt with value", RecordId, baseWall, One(new ShuffleTypedRandomEvent(ShuffleTypedRandomDrawKind.NextInt, 2, 0, 0.5)));
            Reject("nextDouble NaN", RecordId, baseWall, One(ShuffleTypedRandomEvent.ForNextDouble(double.NaN)));
            Reject("nextDouble +Infinity", RecordId, baseWall, One(ShuffleTypedRandomEvent.ForNextDouble(double.PositiveInfinity)));
            Reject("nextDouble -Infinity", RecordId, baseWall, One(ShuffleTypedRandomEvent.ForNextDouble(double.NegativeInfinity)));
            Reject("nextDouble negative", RecordId, baseWall, One(ShuffleTypedRandomEvent.ForNextDouble(-0.0000000000000001)));
            Reject("nextDouble one", RecordId, baseWall, One(ShuffleTypedRandomEvent.ForNextDouble(1.0)));
            Reject("nextDouble above one", RecordId, baseWall, One(ShuffleTypedRandomEvent.ForNextDouble(2.0)));
            Reject("nextDouble with bound", RecordId, baseWall, One(new ShuffleTypedRandomEvent(ShuffleTypedRandomDrawKind.NextDouble, 1, 0, 0.5)));
            Reject("nextDouble with result", RecordId, baseWall, One(new ShuffleTypedRandomEvent(ShuffleTypedRandomDrawKind.NextDouble, 0, 1, 0.5)));

            // 合法边界值必须接受：最小/最大 bound 与 result、0.0 与最大可表示 < 1.0。
            var boundary = new List<ShuffleTypedRandomEvent>
            {
                ShuffleTypedRandomEvent.ForNextInt(1, 0),
                ShuffleTypedRandomEvent.ForNextInt(int.MaxValue, int.MaxValue - 1),
                ShuffleTypedRandomEvent.ForNextDouble(0.0),
                ShuffleTypedRandomEvent.ForNextDouble(0.9999999999999999)
            };
            ShuffleTypedRandomTrace boundaryTrace = ShuffleTypedRandomTrace.Create(RecordId, baseWall, boundary);
            Assert.Equal(4, boundaryTrace.Count);
            Assert.Equal(int.MaxValue, boundaryTrace.Draws[1].Bound);
            Assert.Equal(int.MaxValue - 1, boundaryTrace.Draws[1].Result);
            Assert.Equal(0.0, boundaryTrace.Draws[2].Value);
            Assert.Equal(0.9999999999999999, boundaryTrace.Draws[3].Value);
        }

        private static TileId[] Duplicate(TileId[] wall)
        {
            var copy = (TileId[])wall.Clone();
            copy[3] = copy[2];
            return copy;
        }

        private static List<ShuffleTypedRandomEvent> One(ShuffleTypedRandomEvent draw)
        {
            return new List<ShuffleTypedRandomEvent> { draw };
        }

        // ------------------------------------------------------------------
        // 无目标 / 禁用 / 四人恒等：typed 消费合同
        // ------------------------------------------------------------------

        [Fact]
        public void IdentityPathsRequireExactEmptyTraceAndConsumeNothing()
        {
            var noTargetPlans = Plans(
                (0, ShuffleTarget.Kokushi), (1, ShuffleTarget.Daisangen),
                (2, ShuffleTarget.ChinitsuMan), (3, ShuffleTarget.Daisuushii))
                .ConvertAll(p => new SeatShufflePlan(p.Seat, new ShuffleSelection()));
            var fourPlans = Plans(
                (0, ShuffleTarget.Kokushi), (1, ShuffleTarget.Daisangen),
                (2, ShuffleTarget.ChinitsuMan), (3, ShuffleTarget.Daisuushii));
            var disabledPlans = Plans((0, ShuffleTarget.ChinitsuMan));
            var oneSurplus = new[] { ShuffleTypedRandomEvent.ForNextDouble(0.5) };

            for (int dealer = 0; dealer <= 3; dealer++)
            {
                TileId[] wall = RandomWall(dealer);

                // 无有效目标：空 trace 成功且恒等。
                ShuffleTypedRandomTrace noTargetTrace = ShuffleTypedRandomTrace.Create(RecordId, wall, Array.Empty<ShuffleTypedRandomEvent>());
                ShuffleSynthesisResult noTarget = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                    noTargetTrace, dealer, noTargetPlans, EnabledConfig);
                Assert.True(noTarget.IsSuccess);
                Assert.Equal(0, noTarget.ConsumedRandomCount);
                Assert.Equal(0, noTarget.ActivePlanCount);
                Assert.Equal(wall.ToList(), noTarget.Wall!.WallOrder.ToList());
                Assert.Empty(noTarget.RandomRequests);

                // 多余事件必须被拒绝，且不算作已消费。
                ShuffleTypedRandomTrace noTargetSurplus = ShuffleTypedRandomTrace.Create(RecordId, wall, oneSurplus);
                AssertFailure(
                    ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(noTargetSurplus, dealer, noTargetPlans, EnabledConfig),
                    ShuffleSynthesisErrorCode.TraceNotFullyConsumed,
                    0);

                // 禁用配置：即使有 target 也不进入决策，空 trace 合法。
                ShuffleTypedRandomTrace disabledTrace = ShuffleTypedRandomTrace.Create(RecordId, wall, Array.Empty<ShuffleTypedRandomEvent>());
                ShuffleSynthesisResult disabled = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                    disabledTrace, dealer, disabledPlans, new ShuffleConfig(enabled: false));
                Assert.True(disabled.IsSuccess);
                Assert.Equal(0, disabled.ConsumedRandomCount);
                Assert.Equal(wall.ToList(), disabled.Wall!.WallOrder.ToList());

                // 四人恒等：空 trace 成功；多余 trace 零副作用拒绝。
                ShuffleTypedRandomTrace fourTrace = ShuffleTypedRandomTrace.Create(RecordId, wall, Array.Empty<ShuffleTypedRandomEvent>());
                ShuffleSynthesisResult four = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                    fourTrace, dealer, fourPlans, EnabledConfig);
                Assert.True(four.IsSuccess);
                Assert.Equal(0, four.ConsumedRandomCount);
                Assert.Equal(4, four.ActivePlanCount);
                Assert.Equal(wall.ToList(), four.Wall!.WallOrder.ToList());

                ShuffleTypedRandomTrace fourSurplus = ShuffleTypedRandomTrace.Create(RecordId, wall, oneSurplus);
                AssertFailure(
                    ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(fourSurplus, dealer, fourPlans, EnabledConfig),
                    ShuffleSynthesisErrorCode.TraceNotFullyConsumed,
                    0);
            }
        }

        // ------------------------------------------------------------------
        // kind / bound 不匹配：结构化失败、精确 consumed、输入不变
        // ------------------------------------------------------------------

        [Fact]
        public void KindAndBoundMismatchesFailAtTheExactOrderedPosition()
        {
            var plans = ThreePlans();
            int dealer = 1;
            (ShuffleTypedRandomTrace trace, RecordedRandomDraw legacyWall) = MakeTypedTrace(
                dealer, plans, EnabledConfig, wallSeed: 11, doubleSeed: 1111);
            Assert.True(trace.Count >= 4, "three-caster trace should contain several ordered decisions");

            // 独立于请求日志核对 V4 的席位洗牌边界：长度 3 的 shuffled 依次消耗 nextInt(3)、nextInt(2)。
            Assert.Equal(ShuffleTypedRandomDrawKind.NextInt, trace.Draws[0].Kind);
            Assert.Equal(3, trace.Draws[0].Bound);
            Assert.Equal(ShuffleTypedRandomDrawKind.NextInt, trace.Draws[1].Kind);
            Assert.Equal(2, trace.Draws[1].Bound);

            ShuffleSynthesisResult baseline = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(trace, dealer, plans, EnabledConfig);
            Assert.True(baseline.IsSuccess);
            Assert.Equal(trace.Count, baseline.ConsumedRandomCount);
            Assert.Equal(legacyWall.WallOrder.ToList(), baseline.Wall!.WallOrder.ToList());
            Assert.Equal(trace.Count, baseline.RandomRequests.Count);
            for (int i = 0; i < trace.Count; i++)
            {
                AssertSameDraw(trace.Draws[i], baseline.RandomRequests[i]);
            }

            // 首事件 kind 互换：期望 nextInt，实际 nextDouble。
            List<ShuffleTypedRandomEvent> firstKind = EventsOf(trace);
            firstKind[0] = ShuffleTypedRandomEvent.ForNextDouble(0.5);
            ShuffleTypedRandomTrace firstKindTrace = ShuffleTypedRandomTrace.Create(RecordId, trace.BaseWall.WallOrder, firstKind);
            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(firstKindTrace, dealer, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.UnexpectedTypedDrawKind,
                0);

            // 首事件 bound 错误：0 个事件被消费。
            List<ShuffleTypedRandomEvent> firstBound = EventsOf(trace);
            ShuffleTypedRandomEvent original0 = firstBound[0];
            firstBound[0] = ShuffleTypedRandomEvent.ForNextInt(original0.Bound + 1, original0.Result);
            ShuffleTypedRandomTrace firstBoundTrace = ShuffleTypedRandomTrace.Create(RecordId, trace.BaseWall.WallOrder, firstBound);
            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(firstBoundTrace, dealer, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.TypedDrawBoundMismatch,
                0);

            // 第二个 NextInt 的 bound 错误：第一个事件成功消费，consumed 精确为 1。
            List<ShuffleTypedRandomEvent> midBound = EventsOf(trace);
            Assert.Equal(ShuffleTypedRandomDrawKind.NextInt, midBound[1].Kind);
            ShuffleTypedRandomEvent original1 = midBound[1];
            midBound[1] = ShuffleTypedRandomEvent.ForNextInt(original1.Bound + 1, original1.Result);
            ShuffleTypedRandomTrace midBoundTrace = ShuffleTypedRandomTrace.Create(RecordId, trace.BaseWall.WallOrder, midBound);
            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(midBoundTrace, dealer, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.TypedDrawBoundMismatch,
                1);

            // 首个 nextDouble 被换成 nextInt：期望位置的 double 事件未被消费。
            int firstDouble = -1;
            for (int i = 0; i < trace.Count; i++)
            {
                if (trace.Draws[i].Kind == ShuffleTypedRandomDrawKind.NextDouble)
                {
                    firstDouble = i;
                    break;
                }
            }

            Assert.True(firstDouble >= 0, "three-caster trace must contain a nextDouble decision");
            List<ShuffleTypedRandomEvent> doubleKind = EventsOf(trace);
            doubleKind[firstDouble] = ShuffleTypedRandomEvent.ForNextInt(4, 0);
            ShuffleTypedRandomTrace doubleKindTrace = ShuffleTypedRandomTrace.Create(
                RecordId, trace.BaseWall.WallOrder, doubleKind);
            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(doubleKindTrace, dealer, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.UnexpectedTypedDrawKind,
                firstDouble);

            // 失败不修改来源记录：原 trace 仍可逐张成功回放。
            ShuffleSynthesisResult replay = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(trace, dealer, plans, EnabledConfig);
            Assert.True(replay.IsSuccess);
            Assert.Equal(baseline.Wall!.WallOrder.ToList(), replay.Wall!.WallOrder.ToList());
        }

        // ------------------------------------------------------------------
        // 非恒等合成：136/赤五/死山、方案排列正控制、逐张确定性、legacy 兼容
        // ------------------------------------------------------------------

        [Fact]
        public void TypedSynthesisIsDeterministicNonIdentityAndPreservesPhysicalLaws()
        {
            var plans = ThreePlans();
            int changed = 0;

            for (int seed = 0; seed < 16; seed++)
            {
                int dealer = seed % 4;
                (ShuffleTypedRandomTrace trace, RecordedRandomDraw legacyWall) = MakeTypedTrace(
                    dealer, plans, EnabledConfig, wallSeed: seed, doubleSeed: seed + 9000);

                Assert.True(trace.Count > 0, "three-caster trace must consume decisions");
                ShuffleSynthesisResult first = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                    trace, dealer, plans, EnabledConfig);
                ShuffleSynthesisResult second = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                    trace, dealer, plans, EnabledConfig);
                Assert.True(first.IsSuccess, first.Error?.ToString());
                Assert.True(second.IsSuccess, second.Error?.ToString());
                Assert.Equal(trace.Count, first.ConsumedRandomCount);
                Assert.Equal(3, first.ActivePlanCount);

                IReadOnlyList<TileId> shaped = first.Wall!.WallOrder;
                Assert.Equal(136, shaped.Count);
                Assert.Equal(136, shaped.Select(t => t.Value).Distinct().Count());
                Assert.Equal(TileId.All.ToList(), shaped.OrderBy(t => t.Value).ToList());
                Assert.Equal(3, shaped.Count(t => t.IsRedFive));

                // 同输入重复回放逐张一致；legacy 同一算法结果也逐张一致（C# 自测）。
                Assert.Equal(shaped.ToList(), second.Wall!.WallOrder.ToList());
                Assert.Equal(legacyWall.WallOrder.ToList(), shaped.ToList());

                // 死山 122..135 与基础墙逐张相等。
                for (int slot = ShuffleSlots.DeadWallStart; slot <= ShuffleSlots.DeadWallEnd; slot++)
                {
                    Assert.Equal(trace.BaseWall.WallOrder[slot], shaped[slot]);
                }

                // 方案排列正控制：reversed 输入 + 同一 typed 记录 → 逐张一致。
                ShuffleSynthesisResult reversed = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                    trace, dealer, plans.AsEnumerable().Reverse().ToList(), EnabledConfig);
                Assert.True(reversed.IsSuccess);
                Assert.Equal(shaped.ToList(), reversed.Wall!.WallOrder.ToList());

                if (!shaped.SequenceEqual(trace.BaseWall.WallOrder))
                {
                    changed++;
                }
            }

            Assert.True(changed > 0, "typed entry must perform real non-identity synthesis");
        }

        // ------------------------------------------------------------------
        // 零额度：仍先洗牌；空 trace 不是成功，合法 trace 消耗 > 0 且牌山恒等
        // ------------------------------------------------------------------

        [Fact]
        public void ZeroBudgetIdentityStillConsumesShuffleDecisionsAndRejectsEmptyTrace()
        {
            var plans = Plans((0, ShuffleTarget.ChinitsuMan));
            ShuffleConfig zeroConfig = EnabledConfig.WithTier(1, new ShuffleTier(1.0, 0, 0));

            for (int seed = 0; seed < 8; seed++)
            {
                int dealer = seed % 4;
                (ShuffleTypedRandomTrace trace, RecordedRandomDraw legacyWall) = MakeTypedTrace(
                    dealer, plans, zeroConfig, wallSeed: seed, doubleSeed: seed + 4000);

                Assert.True(trace.Count > 0, "zero-budget path still shuffles seats/slots");
                Assert.All(trace.Draws, draw => Assert.Equal(ShuffleTypedRandomDrawKind.NextInt, draw.Kind));

                // 独立核对 V4 的 Fisher-Yates 边界序列（不依赖 RandomRequests）：
                // n=1 无席位洗牌；起手槽长度 L 依次消耗 nextInt(L)..nextInt(2)，
                // 摸牌槽长度 D 依次消耗 nextInt(D)..nextInt(2)。
                IReadOnlyList<int> initialSlots = ShuffleSlots.Initial(dealer, 0);
                IReadOnlyList<int> drawSlots = ShuffleSlots.Draws(dealer, 0, 18);
                Assert.Equal(initialSlots.Count - 1 + drawSlots.Count - 1, trace.Count);
                int drawIndex = 0;
                for (int bound = initialSlots.Count; bound >= 2; bound--)
                {
                    Assert.Equal(ShuffleTypedRandomDrawKind.NextInt, trace.Draws[drawIndex].Kind);
                    Assert.Equal(bound, trace.Draws[drawIndex].Bound);
                    drawIndex++;
                }

                for (int bound = drawSlots.Count; bound >= 2; bound--)
                {
                    Assert.Equal(ShuffleTypedRandomDrawKind.NextInt, trace.Draws[drawIndex].Kind);
                    Assert.Equal(bound, trace.Draws[drawIndex].Bound);
                    drawIndex++;
                }

                Assert.Equal(trace.Count, drawIndex);

                ShuffleTypedRandomTrace empty = ShuffleTypedRandomTrace.Create(
                    RecordId, trace.BaseWall.WallOrder, Array.Empty<ShuffleTypedRandomEvent>());
                AssertFailure(
                    ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(empty, dealer, plans, zeroConfig),
                    ShuffleSynthesisErrorCode.TraceExhausted,
                    0);

                ShuffleSynthesisResult result = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                    trace, dealer, plans, zeroConfig);
                Assert.True(result.IsSuccess, result.Error?.ToString());
                Assert.Equal(trace.Count, result.ConsumedRandomCount);
                Assert.Equal(1, result.ActivePlanCount);
                Assert.Equal(trace.BaseWall.WallOrder.ToList(), result.Wall!.WallOrder.ToList());
                Assert.Equal(legacyWall.WallOrder.ToList(), result.Wall!.WallOrder.ToList());
            }
        }

        // ------------------------------------------------------------------
        // 不足 / 多余 / 跳过 / 重复 / 顺序错乱
        // ------------------------------------------------------------------

        [Fact]
        public void MissingExtraSkippedDuplicatedAndReorderedEventsAreRejected()
        {
            var plans = ThreePlans();
            int dealer = 2;
            (ShuffleTypedRandomTrace trace, _) = MakeTypedTrace(
                dealer, plans, EnabledConfig, wallSeed: 12, doubleSeed: 1212);
            List<ShuffleTypedRandomEvent> events = EventsOf(trace);
            int count = events.Count;
            Assert.True(count > 3);

            // 遗漏最后一个事件：消耗 count-1 后耗尽。
            ShuffleTypedRandomTrace truncated = ShuffleTypedRandomTrace.Create(
                RecordId, trace.BaseWall.WallOrder, events.Take(count - 1).ToList());
            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(truncated, dealer, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.TraceExhausted,
                count - 1);

            // 多余事件：全部合法事件消费完后仍有剩余。
            List<ShuffleTypedRandomEvent> surplus = new List<ShuffleTypedRandomEvent>(events)
            {
                ShuffleTypedRandomEvent.ForNextDouble(0.5)
            };
            ShuffleTypedRandomTrace surplusTrace = ShuffleTypedRandomTrace.Create(
                RecordId, trace.BaseWall.WallOrder, surplus);
            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(surplusTrace, dealer, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.TraceNotFullyConsumed,
                count);

            // 跳过首个事件：第一个请求就与记录不匹配，consumed 0。
            ShuffleTypedRandomTrace skipped = ShuffleTypedRandomTrace.Create(
                RecordId, trace.BaseWall.WallOrder, events.Skip(1).ToList());
            ShuffleSynthesisResult skippedResult = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                skipped, dealer, plans, EnabledConfig);
            Assert.False(skippedResult.IsSuccess);
            Assert.Null(skippedResult.Wall);
            Assert.Equal(0, skippedResult.ConsumedRandomCount);
            Assert.True(
                skippedResult.Error!.Code == ShuffleSynthesisErrorCode.UnexpectedTypedDrawKind ||
                skippedResult.Error.Code == ShuffleSynthesisErrorCode.TypedDrawBoundMismatch,
                "skipped event must surface as an ordered kind/bound mismatch, got " + skippedResult.Error);

            // 重复首个事件：第一条被接受，第二条与下一请求不匹配，consumed 1。
            List<ShuffleTypedRandomEvent> duplicated = new List<ShuffleTypedRandomEvent>(events);
            duplicated.Insert(0, events[0]);
            ShuffleTypedRandomTrace duplicatedTrace = ShuffleTypedRandomTrace.Create(
                RecordId, trace.BaseWall.WallOrder, duplicated);
            ShuffleSynthesisResult duplicatedResult = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                duplicatedTrace, dealer, plans, EnabledConfig);
            Assert.False(duplicatedResult.IsSuccess);
            Assert.Null(duplicatedResult.Wall);
            Assert.Equal(1, duplicatedResult.ConsumedRandomCount);
            Assert.True(
                duplicatedResult.Error!.Code == ShuffleSynthesisErrorCode.UnexpectedTypedDrawKind ||
                duplicatedResult.Error.Code == ShuffleSynthesisErrorCode.TypedDrawBoundMismatch,
                "duplicated event must surface as an ordered mismatch, got " + duplicatedResult.Error);

            // 交换前两个事件：顺序被破坏，第一条即不匹配。
            List<ShuffleTypedRandomEvent> reordered = new List<ShuffleTypedRandomEvent>(events);
            (reordered[0], reordered[1]) = (reordered[1], reordered[0]);
            ShuffleTypedRandomTrace reorderedTrace = ShuffleTypedRandomTrace.Create(
                RecordId, trace.BaseWall.WallOrder, reordered);
            ShuffleSynthesisResult reorderedResult = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                reorderedTrace, dealer, plans, EnabledConfig);
            Assert.False(reorderedResult.IsSuccess);
            Assert.Null(reorderedResult.Wall);
            Assert.Equal(0, reorderedResult.ConsumedRandomCount);
        }

        // ------------------------------------------------------------------
        // 批量单字段负例矩阵：任何一项被接受即让本 Fact 失败（native exit 非零）
        // ------------------------------------------------------------------

        [Fact]
        public void BatchSingleFieldMutationMatrixRejectsEveryCorruption()
        {
            var plans = ThreePlans();
            int dealer = 3;
            (ShuffleTypedRandomTrace trace, _) = MakeTypedTrace(
                dealer, plans, EnabledConfig, wallSeed: 13, doubleSeed: 1313);
            List<ShuffleTypedRandomEvent> events = EventsOf(trace);
            Assert.True(events.Count > 3);

            int firstDouble = -1;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Kind == ShuffleTypedRandomDrawKind.NextDouble)
                {
                    firstDouble = i;
                    break;
                }
            }

            Assert.True(firstDouble >= 0);
            ShuffleTypedRandomEvent first = events[0];
            Assert.Equal(ShuffleTypedRandomDrawKind.NextInt, first.Kind);

            var cases = new List<(string Name, List<ShuffleTypedRandomEvent> Events)>();
            cases.Add(("first-int-to-double", Replace(events, 0, ShuffleTypedRandomEvent.ForNextDouble(0.5))));
            cases.Add(("first-bound-plus-one", Replace(events, 0, ShuffleTypedRandomEvent.ForNextInt(first.Bound + 1, first.Result))));
            cases.Add(("first-bound-zero", Replace(events, 0, ShuffleTypedRandomEvent.ForNextInt(0, 0))));
            cases.Add(("first-result-at-bound", Replace(events, 0, ShuffleTypedRandomEvent.ForNextInt(first.Bound, first.Bound))));
            cases.Add(("first-int-with-double-value", Replace(events, 0, new ShuffleTypedRandomEvent(
                ShuffleTypedRandomDrawKind.NextInt, first.Bound, first.Result, 0.5))));
            cases.Add(("first-double-to-int", Replace(events, firstDouble, ShuffleTypedRandomEvent.ForNextInt(4, 0))));
            cases.Add(("first-double-NaN", Replace(events, firstDouble, ShuffleTypedRandomEvent.ForNextDouble(double.NaN))));
            cases.Add(("first-double-one", Replace(events, firstDouble, ShuffleTypedRandomEvent.ForNextDouble(1.0))));
            cases.Add(("skip-first", events.Skip(1).ToList()));
            cases.Add(("duplicate-first", Prepend(events, events[0])));
            cases.Add(("drop-last", events.Take(events.Count - 1).ToList()));
            cases.Add(("append-extra", new List<ShuffleTypedRandomEvent>(events) { ShuffleTypedRandomEvent.ForNextDouble(0.5) }));
            cases.Add(("swap-first-two", SwapFirstTwo(events)));

            var accepted = new List<string>();
            foreach ((string name, List<ShuffleTypedRandomEvent> mutated) in cases)
            {
                if (!ShuffleTypedRandomTrace.TryCreate(
                        RecordId, trace.BaseWall.WallOrder, mutated, out ShuffleTypedRandomTrace? candidate, out _))
                {
                    continue;
                }

                ShuffleSynthesisResult result = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                    candidate!, dealer, plans, EnabledConfig);
                if (result.IsSuccess || result.Wall != null)
                {
                    accepted.Add(name);
                }
            }

            Assert.True(
                accepted.Count == 0,
                "single-field corruptions accepted by typed seam: " + string.Join(", ", accepted));
            Assert.Empty(accepted);

            // 矩阵跑完后原记录仍可成功回放，证明负例没有污染输入。
            ShuffleSynthesisResult replay = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                trace, dealer, plans, EnabledConfig);
            Assert.True(replay.IsSuccess);
        }

        private static List<ShuffleTypedRandomEvent> Replace(
            List<ShuffleTypedRandomEvent> source,
            int index,
            ShuffleTypedRandomEvent replacement)
        {
            var copy = new List<ShuffleTypedRandomEvent>(source);
            copy[index] = replacement;
            return copy;
        }

        private static List<ShuffleTypedRandomEvent> Prepend(
            List<ShuffleTypedRandomEvent> source,
            ShuffleTypedRandomEvent prefix)
        {
            var copy = new List<ShuffleTypedRandomEvent> { prefix };
            copy.AddRange(source);
            return copy;
        }

        private static List<ShuffleTypedRandomEvent> SwapFirstTwo(List<ShuffleTypedRandomEvent> source)
        {
            var copy = new List<ShuffleTypedRandomEvent>(source);
            (copy[0], copy[1]) = (copy[1], copy[0]);
            return copy;
        }

        // ------------------------------------------------------------------
        // 入口参数与原始事件列表的结构化失败
        // ------------------------------------------------------------------

        [Fact]
        public void InvalidEntryArgumentsAndRawEventsReturnStructuredFailures()
        {
            var plans = ThreePlans();
            int dealer = 0;
            (ShuffleTypedRandomTrace trace, _) = MakeTypedTrace(
                dealer, plans, EnabledConfig, wallSeed: 14, doubleSeed: 1414);
            List<ShuffleTypedRandomEvent> events = EventsOf(trace);
            TileId[] wall = trace.BaseWall.WallOrder.ToArray();

            Assert.Throws<ArgumentNullException>(
                () => ShuffleWallSynthesizer.ShapeWithTypedRandomTrace((ShuffleTypedRandomTrace)null!, dealer, plans, EnabledConfig));
            Assert.Throws<ArgumentNullException>(
                () => ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(trace, dealer, plans, null!));

            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(trace, -1, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidDealerSeat,
                0);
            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(trace, 4, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidDealerSeat,
                0);
            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(trace, dealer, null!, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidPlanSeat,
                0);
            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(trace, dealer, new List<SeatShufflePlan> { null! }, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidPlanSeat,
                0);
            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                    trace, dealer, Plans((1, ShuffleTarget.Kokushi), (1, ShuffleTarget.Daisangen)), EnabledConfig),
                ShuffleSynthesisErrorCode.DuplicatePlanSeat,
                0);

            // 原始事件入口：记录级非法输入返回 InvalidTypedTrace，Wall=null。
            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace("", wall, events, dealer, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidTypedTrace,
                0);
            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                    RecordId, Duplicate(wall), events, dealer, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidTypedTrace,
                0);
            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                    RecordId, wall, new List<ShuffleTypedRandomEvent> { ShuffleTypedRandomEvent.ForNextDouble(double.NaN) },
                    dealer, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidTypedTrace,
                0);
            AssertFailure(
                ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                    RecordId, wall, null!, dealer, plans, EnabledConfig),
                ShuffleSynthesisErrorCode.InvalidTypedTrace,
                0);

            // 同一合法原始列表必须成功，并与 trace 入口逐张一致。
            ShuffleSynthesisResult raw = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                RecordId, wall, events, dealer, plans, EnabledConfig);
            ShuffleSynthesisResult byTrace = ShuffleWallSynthesizer.ShapeWithTypedRandomTrace(
                trace, dealer, plans, EnabledConfig);
            Assert.True(raw.IsSuccess);
            Assert.True(byTrace.IsSuccess);
            Assert.Equal(byTrace.Wall!.WallOrder.ToList(), raw.Wall!.WallOrder.ToList());
            Assert.Equal(byTrace.ConsumedRandomCount, raw.ConsumedRandomCount);
        }
    }
}
