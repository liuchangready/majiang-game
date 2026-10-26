using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mahjong.Domain.Stacking
{
    /// <summary>码牌合成失败原因码。零副作用失败：失败时 <see cref="ShuffleSynthesisResult.Wall"/> 为 null。</summary>
    public enum ShuffleSynthesisErrorCode
    {
        None = 0,
        InvalidRecordId = 1,
        InvalidBaseWall = 2,
        InvalidTraceDouble = 3,
        InvalidDealerSeat = 4,
        InvalidPlanSeat = 5,
        DuplicatePlanSeat = 6,
        TraceExhausted = 7,
        TraceNotFullyConsumed = 8,

        // 以下为 typed 记录入口新增值；保留 0..8 的既有数值语义。
        InvalidTypedTrace = 9,
        UnexpectedTypedDrawKind = 10,
        TypedDrawBoundMismatch = 11
    }

    /// <summary>结构化失败信息。</summary>
    public sealed class ShuffleSynthesisError
    {
        public ShuffleSynthesisError(ShuffleSynthesisErrorCode code, string message)
        {
            if (code == ShuffleSynthesisErrorCode.None)
            {
                throw new ArgumentOutOfRangeException(nameof(code), code, "A failure requires a non-None code.");
            }

            Code = code;
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        public ShuffleSynthesisErrorCode Code { get; }

        public string Message { get; }

        public override string ToString() => Code + ": " + Message;
    }

    /// <summary>
    /// 码牌合成结果。失败时 <see cref="Wall"/> 为 null 且不产生任何可观察副作用；
    /// 成功时 <see cref="Wall"/> 是完整记录牌山（136 张唯一实体牌），可直接喂给
    /// <see cref="Mahjong.Domain.TilePositionMap.CreateWall(RecordedRandomDraw)"/> 与
    /// <see cref="Mahjong.Domain.TilePositionMap.CreateDealt(RecordedRandomDraw, int)"/>。
    /// </summary>
    public sealed class ShuffleSynthesisResult
    {
        private readonly IReadOnlyList<ShuffleTypedRandomDraw> _randomRequests;

        private ShuffleSynthesisResult(
            bool isSuccess,
            RecordedRandomDraw? wall,
            ShuffleSynthesisError? error,
            int consumedRandomCount,
            int activePlanCount,
            IReadOnlyList<ShuffleTypedRandomDraw>? randomRequests)
        {
            IsSuccess = isSuccess;
            Wall = wall;
            Error = error;
            ConsumedRandomCount = consumedRandomCount;
            ActivePlanCount = activePlanCount;
            _randomRequests = Freeze(randomRequests);
        }

        public bool IsSuccess { get; }

        /// <summary>成功时的最终完整牌山记录（136 张唯一实体牌）。</summary>
        public RecordedRandomDraw? Wall { get; }

        public ShuffleSynthesisError? Error { get; }

        /// <summary>本次合成实际消耗的记录随机值个数（成功与失败均报告）。</summary>
        public int ConsumedRandomCount { get; }

        /// <summary>有效施术人数 n（target 非空的活跃方案数）。</summary>
        public int ActivePlanCount { get; }

        /// <summary>
        /// 本次合成实际接受的随机请求序列（诊断/兼容性证据）。
        ///
        /// 对既有 double-only 入口，它把每次 Fisher-Yates 的 floor 选择记录为
        /// nextInt(bound, result)，把每次概率/供体选择记录为 nextDouble(value)；
        /// 对 typed 入口，它逐项等于来源记录中被消费的事件。失败时也报告已消费的前缀。
        /// 这是 C# 执行请求的只读快照，不是 V4 黄金样本，也不证明跨运行时行为一致。
        /// </summary>
        public IReadOnlyList<ShuffleTypedRandomDraw> RandomRequests => _randomRequests;

        public static ShuffleSynthesisResult Success(
            RecordedRandomDraw wall,
            int consumedRandomCount,
            int activePlanCount)
        {
            return Success(wall, consumedRandomCount, activePlanCount, null);
        }

        public static ShuffleSynthesisResult Success(
            RecordedRandomDraw wall,
            int consumedRandomCount,
            int activePlanCount,
            IReadOnlyList<ShuffleTypedRandomDraw>? randomRequests)
        {
            if (wall == null)
            {
                throw new ArgumentNullException(nameof(wall));
            }

            return new ShuffleSynthesisResult(true, wall, null, consumedRandomCount, activePlanCount, randomRequests);
        }

        public static ShuffleSynthesisResult Failure(
            ShuffleSynthesisErrorCode code,
            string message,
            int consumedRandomCount,
            int activePlanCount)
        {
            return Failure(code, message, consumedRandomCount, activePlanCount, null);
        }

        public static ShuffleSynthesisResult Failure(
            ShuffleSynthesisErrorCode code,
            string message,
            int consumedRandomCount,
            int activePlanCount,
            IReadOnlyList<ShuffleTypedRandomDraw>? randomRequests)
        {
            return new ShuffleSynthesisResult(
                false,
                null,
                new ShuffleSynthesisError(code, message),
                consumedRandomCount,
                activePlanCount,
                randomRequests);
        }

        private static IReadOnlyList<ShuffleTypedRandomDraw> Freeze(
            IReadOnlyList<ShuffleTypedRandomDraw>? randomRequests)
        {
            if (randomRequests == null || randomRequests.Count == 0)
            {
                return Array.Empty<ShuffleTypedRandomDraw>();
            }

            var copy = new ShuffleTypedRandomDraw[randomRequests.Count];
            for (int i = 0; i < randomRequests.Count; i++)
            {
                copy[i] = randomRequests[i];
            }

            return Array.AsReadOnly(copy);
        }
    }

    /// <summary>
    /// 确定性统一码牌千术牌山合成器。
    ///
    /// V4 <c>com.majiang.stacking.ShuffleWallGenerator</c> 决策协议的逐值移植，唯一差异是
    /// 随机来源：V4 用 <c>Random(seed)</c> 内部派生（JVM 专属，开放决策 D-09），V5 依照
    /// “随机结果作为明确输入记录”的契约改由 <see cref="ShuffleRandomTrace"/> 或
    /// <see cref="ShuffleTypedRandomTrace"/> 供给。两个入口共享同一决策算法：
    /// - 席位/槽位洗牌：Fisher-Yates，长度 m 的序列消耗 m-1 个有界整数决策（bound = i + 1）；
    /// - 小数额度最后一搏：<c>double >= remaining</c> 则放弃；
    /// - 熟练度判定：<c>double >= strength</c> 则跳过该槽；
    /// - 供体抽样：<c>double * totalWeight</c> 做 Delta^2 加权选择。
    /// 既有 double-only 入口把这些有界决策保持为 floor(double * (i + 1)) 以维持旧结果；
    /// typed 入口直接消费 nextInt(bound) 的整数结果，避免把 int 伪装成 double。
    /// 轨迹消耗必须恰好用尽：不足/多余都是记录与输入不匹配的失败。
    ///
    /// 核心设计与物理守恒规则（与 V4 KDoc 一致）：
    /// 1. 严格 136 张物理守恒：基础牌序经逐张校验（重复/缺失拒绝），交换只做槽位对调；
    /// 2. 零千术不变律：无出千方案（全部 target==null 或 config.Enabled==false）或空方案
    ///    列表直接返回基础洗牌，消耗 0 个记录值；
    /// 3. 排序无关性：输入 plans 列表顺序不影响结果（按 seat 升序规范化）；
    /// 4. 四人恒等律：n==4 时直接返回基础洗牌，不做任何交换，逐张相等；
    /// 5. 王牌隔离律：王牌区 122..135 绝不作目标槽，也绝不作交换供体来源；
    /// 6. 槽位归属：玩家只优化本人槽位（起手 0..52，摸牌 53..121）；起手完成后 0..52 整体锁定；
    /// 7. 人数档位强度与成功交换额度：按有效施术人数 n 取 ShuffleConfig 档位，失败尝试不耗额度；
    /// 8. 多人冲突衰减：p_i = s_i / (1 + lambda * Σ s_j * cos + diversity * Σ s_j * (1 - cos))，限于 [0,1]；
    /// 9. 供体保守规则：只从非已提交、非王牌的活牌山槽取供体；本人尚待处理槽绝不可借用，
    ///    其他施术者尚待处理槽仅在换入牌不降低其目标效用时才可借用。
    /// </summary>
    public static class ShuffleWallSynthesizer
    {
        /// <summary>
        /// 从显式记录的随机输入确定性合成牌山（原始输入入口；校验全部失败原因码）。
        /// </summary>
        public static ShuffleSynthesisResult Shape(
            string recordId,
            IReadOnlyList<TileId> baseWallOrder,
            IReadOnlyList<double> recordedDoubles,
            int dealerSeat,
            IReadOnlyList<SeatShufflePlan> plans,
            ShuffleConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (string.IsNullOrEmpty(recordId))
            {
                return Failure(ShuffleSynthesisErrorCode.InvalidRecordId, "recordId must be non-empty.", 0, 0);
            }

            if (baseWallOrder == null)
            {
                return Failure(ShuffleSynthesisErrorCode.InvalidBaseWall, "base wall order must not be null.", 0, 0);
            }

            if (!ShuffleRandomTrace.ValidateBaseWall(baseWallOrder, out string? baseWallError))
            {
                return Failure(ShuffleSynthesisErrorCode.InvalidBaseWall, baseWallError!, 0, 0);
            }

            if (recordedDoubles == null)
            {
                return Failure(ShuffleSynthesisErrorCode.InvalidTraceDouble, "recorded doubles must not be null.", 0, 0);
            }

            if (!ShuffleRandomTrace.ValidateDoubles(recordedDoubles, out string? doubleError))
            {
                return Failure(ShuffleSynthesisErrorCode.InvalidTraceDouble, doubleError!, 0, 0);
            }

            if (dealerSeat < 0 || dealerSeat > 3)
            {
                return Failure(
                    ShuffleSynthesisErrorCode.InvalidDealerSeat,
                    "dealerSeat must be 0..3, got " + dealerSeat.ToString(CultureInfo.InvariantCulture) + ".",
                    0,
                    0);
            }

            if (plans == null)
            {
                return Failure(ShuffleSynthesisErrorCode.InvalidPlanSeat, "plans must not be null.", 0, 0);
            }

            for (int i = 0; i < plans.Count; i++)
            {
                SeatShufflePlan? plan = plans[i];
                if (plan == null || plan.Seat < 0 || plan.Seat > 3)
                {
                    return Failure(
                        ShuffleSynthesisErrorCode.InvalidPlanSeat,
                        "plan at index " + i.ToString(CultureInfo.InvariantCulture) + " has an invalid seat.",
                        0,
                        0);
                }
            }

            for (int i = 0; i < plans.Count; i++)
            {
                for (int j = i + 1; j < plans.Count; j++)
                {
                    if (plans[i].Seat == plans[j].Seat)
                    {
                        return Failure(
                            ShuffleSynthesisErrorCode.DuplicatePlanSeat,
                            "duplicate plan seat " + plans[i].Seat.ToString(CultureInfo.InvariantCulture) + ".",
                            0,
                            0);
                    }
                }
            }

            var reader = new DoubleTraceReader(recordedDoubles);
            try
            {
                return ShapeValidated(recordId, baseWallOrder, reader, dealerSeat, plans, config);
            }
            catch (TraceExhaustedSignal)
            {
                return Failure(
                    ShuffleSynthesisErrorCode.TraceExhausted,
                    "recorded doubles exhausted before synthesis completed; the record does not match this input.",
                    0,
                    0,
                    reader.Requests);
            }
        }

        /// <summary>从持久化记录回放（等价于拆解 <see cref="ShuffleRandomTrace"/> 后的原始输入入口）。</summary>
        public static ShuffleSynthesisResult Shape(
            ShuffleRandomTrace trace,
            int dealerSeat,
            IReadOnlyList<SeatShufflePlan> plans,
            ShuffleConfig config)
        {
            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            return Shape(trace.RecordId, trace.BaseWall.WallOrder, trace.Doubles, dealerSeat, plans, config);
        }

        /// <summary>
        /// 从有序 typed 随机记录回放（D-R1-02 seam）。
        ///
        /// 与既有 double-only 入口共享同一合成算法，但随机消费严格按类型：
        /// - Fisher-Yates 每次消耗 <c>nextInt(i + 1)</c> 并直接使用整数结果；
        /// - 小数额度最后一搏、熟练度判定、供体 Delta^2 抽样消耗 <c>nextDouble()</c>。
        /// 不会把 int 伪装成 double 再 floor，也不会搜索/跳过不匹配事件。kind 或 bound
        /// 不匹配、记录不足或多余都返回结构化失败，<see cref="ShuffleSynthesisResult.Wall"/>
        /// 为 null，输入记录不被修改。ConsumedRandomCount 是在失败前已成功匹配并应用的
        /// 事件数（不包含触发失败的那个事件）。
        /// </summary>
        public static ShuffleSynthesisResult ShapeWithTypedRandomTrace(
            ShuffleTypedRandomTrace trace,
            int dealerSeat,
            IReadOnlyList<SeatShufflePlan> plans,
            ShuffleConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            if (dealerSeat < 0 || dealerSeat > 3)
            {
                return Failure(
                    ShuffleSynthesisErrorCode.InvalidDealerSeat,
                    "dealerSeat must be 0..3, got " + dealerSeat.ToString(CultureInfo.InvariantCulture) + ".",
                    0,
                    0);
            }

            if (plans == null)
            {
                return Failure(ShuffleSynthesisErrorCode.InvalidPlanSeat, "plans must not be null.", 0, 0);
            }

            for (int i = 0; i < plans.Count; i++)
            {
                SeatShufflePlan? plan = plans[i];
                if (plan == null || plan.Seat < 0 || plan.Seat > 3)
                {
                    return Failure(
                        ShuffleSynthesisErrorCode.InvalidPlanSeat,
                        "plan at index " + i.ToString(CultureInfo.InvariantCulture) + " has an invalid seat.",
                        0,
                        0);
                }
            }

            for (int i = 0; i < plans.Count; i++)
            {
                for (int j = i + 1; j < plans.Count; j++)
                {
                    if (plans[i].Seat == plans[j].Seat)
                    {
                        return Failure(
                            ShuffleSynthesisErrorCode.DuplicatePlanSeat,
                            "duplicate plan seat " + plans[i].Seat.ToString(CultureInfo.InvariantCulture) + ".",
                            0,
                            0);
                    }
                }
            }

            var reader = new TypedTraceReader(trace);
            try
            {
                return ShapeValidated(trace.RecordId, trace.BaseWall.WallOrder, reader, dealerSeat, plans, config);
            }
            catch (TypedTraceExhaustedSignal)
            {
                return Failure(
                    ShuffleSynthesisErrorCode.TraceExhausted,
                    "typed random draws exhausted before synthesis completed; the record does not match this input.",
                    reader.Position,
                    0,
                    reader.Requests);
            }
            catch (TypedTraceMismatchSignal mismatch)
            {
                return Failure(mismatch.Code, mismatch.Message, reader.Position, 0, reader.Requests);
            }
        }

        /// <summary>
        /// 从未校验的 typed 事件原始列表回放。记录级非法字段（未知 kind、非法 bound/result、
        /// NaN/Infinity、空/重复基础墙）返回 <see cref="ShuffleSynthesisErrorCode.InvalidTypedTrace"/>，
        /// 不创建部分记录，也不产生牌山。
        /// </summary>
        public static ShuffleSynthesisResult ShapeWithTypedRandomTrace(
            string recordId,
            IReadOnlyList<TileId> baseWallOrder,
            IReadOnlyList<ShuffleTypedRandomEvent> draws,
            int dealerSeat,
            IReadOnlyList<SeatShufflePlan> plans,
            ShuffleConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (!ShuffleTypedRandomTrace.TryCreate(recordId, baseWallOrder, draws, out ShuffleTypedRandomTrace? trace, out string? error))
            {
                return Failure(
                    ShuffleSynthesisErrorCode.InvalidTypedTrace,
                    error ?? "invalid typed shuffle random trace.",
                    0,
                    0);
            }

            return ShapeWithTypedRandomTrace(trace!, dealerSeat, plans, config);
        }

        private static ShuffleSynthesisResult ShapeValidated(
            string recordId,
            IReadOnlyList<TileId> baseWallOrder,
            IRandomDecisionSource reader,
            int dealerSeat,
            IReadOnlyList<SeatShufflePlan> plans,
            ShuffleConfig config)
        {
            // 1. 基础洗牌副本（保证 136 张物理守恒；后续交换全部发生在本副本上）
            var wall = new TileId[TileId.Count];
            for (int i = 0; i < TileId.Count; i++)
            {
                wall[i] = baseWallOrder[i];
            }

            // 2. 规范化方案（按席位排序保证输入顺序无关）
            var canonical = new List<SeatShufflePlan>(plans);
            canonical.Sort((a, b) => a.Seat.CompareTo(b.Seat));

            // 3. 有效施术方案：target 非空才参与做牌（看破抵消后的有效方案由权威层传入）
            var active = new List<SeatShufflePlan>();
            if (config.Enabled)
            {
                foreach (SeatShufflePlan plan in canonical)
                {
                    if (plan.Selection.Target.HasValue)
                    {
                        active.Add(plan);
                    }
                }
            }

            // 若千术未启用或无人做牌，直接返回基础洗牌（与空方案一致），消耗 0 个记录值；
            // 记录序列必须为空——多余值意味着记录与输入不匹配（回放契约与正常路径一致）。
            if (active.Count == 0)
            {
                if (reader.Count != 0)
                {
                    return ShuffleSynthesisResult.Failure(
                        ShuffleSynthesisErrorCode.TraceNotFullyConsumed,
                        reader.TraceLabel + " not fully consumed: consumed 0 of " +
                        reader.Count.ToString(CultureInfo.InvariantCulture) +
                        "; the record does not match this input.",
                        0,
                        0);
                }

                return ShuffleSynthesisResult.Success(RecordedRandomDraw.Create(recordId, wall), 0, 0, reader.Requests);
            }

            int n = active.Count;

            // 4. 四人档硬性恒等：四人均有效施术时直接返回基础洗牌，不做任何交换；
            // 同样必须拒绝多余记录值。
            if (n == 4)
            {
                if (reader.Count != 0)
                {
                    return ShuffleSynthesisResult.Failure(
                        ShuffleSynthesisErrorCode.TraceNotFullyConsumed,
                        reader.TraceLabel + " not fully consumed: consumed 0 of " +
                        reader.Count.ToString(CultureInfo.InvariantCulture) +
                        "; the record does not match this input.",
                        0,
                        4);
                }

                return ShuffleSynthesisResult.Success(RecordedRandomDraw.Create(recordId, wall), 0, 4, reader.Requests);
            }

            // 同目标桌按目标取覆盖档；存在异目标时全桌改用混合专用档。
            bool mixedTargetTable = CountDistinctTargets(active) > 1;
            var tierBySeat = new Dictionary<int, ShuffleTier>(n);
            foreach (SeatShufflePlan plan in active)
            {
                ShuffleTier? tier = mixedTargetTable
                    ? config.MixedTierFor(n)
                    : config.TierFor(n, plan.Selection.Target!.Value);
                if (!tier.HasValue)
                {
                    throw new InvalidOperationException(
                        "tier for n=" + n.ToString(CultureInfo.InvariantCulture) + " must be non-null.");
                }

                tierBySeat[plan.Seat] = tier.Value;
            }

            // 5. 每席一个 34 维需求向量，只计算一次有效熟练度（同目标冲突 + 异目标分化损耗）
            Dictionary<int, double> strengths = ComputeEffectiveStrengths(
                active,
                tierBySeat,
                config.Interference,
                config.DiversityInterference);

            // 6. 槽位映射（本人起手槽 / 本人预计摸牌槽）
            var initialSlotsMap = new Dictionary<int, int[]>();
            var drawSlotsMap = new Dictionary<int, int[]>();
            for (int seat = 0; seat <= 3; seat++)
            {
                initialSlotsMap[seat] = CopySlots(ShuffleSlots.Initial(dealerSeat, seat));
                drawSlotsMap[seat] = CopySlots(ShuffleSlots.Draws(dealerSeat, seat, config.PlannedDraws));
            }

            // 目标槽 -> 施术席位。其他施术者尚待处理的目标槽只有在“换入牌不降低其目标效用”时才可借用。
            var targetOwners = new Dictionary<int, int>();
            foreach (SeatShufflePlan plan in active)
            {
                foreach (int slot in initialSlotsMap[plan.Seat])
                {
                    targetOwners[slot] = plan.Seat;
                }

                foreach (int slot in drawSlotsMap[plan.Seat])
                {
                    targetOwners[slot] = plan.Seat;
                }
            }

            var planBySeat = new Dictionary<int, SeatShufflePlan>(n);
            foreach (SeatShufflePlan plan in active)
            {
                planBySeat[plan.Seat] = plan;
            }

            var committedSlots = new HashSet<int>();

            // 各席位成功交换额度（失败尝试不耗额度）。异目标按分化损耗缩减为小数额度。
            Dictionary<int, double> budgetScaleBySeat = ComputeBudgetScales(active, config.DiversityBudgetPenaltyFor(n));
            var initialBudget = new Dictionary<int, double>(n);
            var drawBudget = new Dictionary<int, double>(n);
            foreach (SeatShufflePlan plan in active)
            {
                initialBudget[plan.Seat] = tierBySeat[plan.Seat].InitialBudget * budgetScaleBySeat[plan.Seat];
                drawBudget[plan.Seat] = tierBySeat[plan.Seat].DrawBudget * budgetScaleBySeat[plan.Seat];
            }

            // 7. 阶段一：起手发牌槽优化 (0..52)。随机打乱活跃席位与本人槽位。
            int[] activeSeats = new int[n];
            for (int i = 0; i < n; i++)
            {
                activeSeats[i] = active[i].Seat;
            }

            int[] phaseOneSeats = (int[])activeSeats.Clone();
            ShuffledInPlace(phaseOneSeats, reader);
            foreach (int seat in phaseOneSeats)
            {
                SeatShufflePlan plan = planBySeat[seat];
                int[] slots = (int[])initialSlotsMap[seat].Clone();
                ShuffledInPlace(slots, reader);
                double strength = strengths[seat];
                foreach (int targetSlot in slots)
                {
                    double remaining = initialBudget[seat];
                    if (remaining <= 0.0)
                    {
                        break;
                    }

                    // 异目标额度可能是小数：按剩余额度概率允许最后一次尝试，得到期望额度。
                    if (remaining < 1.0 && reader.NextDouble() >= remaining)
                    {
                        break;
                    }

                    committedSlots.Add(targetSlot);

                    // 有效熟练度只决定一次“正收益交换尝试”是否发生；失败不耗成功额度。
                    if (reader.NextDouble() >= strength)
                    {
                        continue;
                    }

                    var contextTiles = new List<TileId>(13);
                    foreach (int s in initialSlotsMap[seat])
                    {
                        if (s != targetSlot)
                        {
                            contextTiles.Add(wall[s]);
                        }
                    }

                    bool swapped = ImproveSlot(
                        wall,
                        targetSlot,
                        seat,
                        plan.Selection.Target!.Value,
                        contextTiles,
                        committedSlots,
                        targetOwners,
                        planBySeat,
                        initialSlotsMap,
                        drawSlotsMap,
                        reader);
                    if (swapped)
                    {
                        initialBudget[seat] = initialBudget[seat] - 1.0;
                    }
                }
            }

            // 起手发牌完毕后，所有起手槽位 (0..52) 锁定，摸牌阶段绝不可从已发手牌抽 donor。
            for (int s = 0; s < ShuffleSlots.InitialDealTiles; s++)
            {
                committedSlots.Add(s);
            }

            // 8. 阶段二：后续预计摸牌槽优化 (53..121)。
            int[] phaseTwoSeats = (int[])activeSeats.Clone();
            ShuffledInPlace(phaseTwoSeats, reader);
            foreach (int seat in phaseTwoSeats)
            {
                SeatShufflePlan plan = planBySeat[seat];
                int[] slots = (int[])drawSlotsMap[seat].Clone();
                ShuffledInPlace(slots, reader);
                double strength = strengths[seat];
                foreach (int targetSlot in slots)
                {
                    double remaining = drawBudget[seat];
                    if (remaining <= 0.0)
                    {
                        break;
                    }

                    if (remaining < 1.0 && reader.NextDouble() >= remaining)
                    {
                        break;
                    }

                    committedSlots.Add(targetSlot);
                    if (reader.NextDouble() >= strength)
                    {
                        continue;
                    }

                    List<TileId> contextTiles = DrawVirtualHand(
                        wall,
                        targetSlot,
                        initialSlotsMap[seat],
                        drawSlotsMap[seat],
                        plan.Selection.Target!.Value);
                    bool swapped = ImproveSlot(
                        wall,
                        targetSlot,
                        seat,
                        plan.Selection.Target!.Value,
                        contextTiles,
                        committedSlots,
                        targetOwners,
                        planBySeat,
                        initialSlotsMap,
                        drawSlotsMap,
                        reader);
                    if (swapped)
                    {
                        drawBudget[seat] = drawBudget[seat] - 1.0;
                    }
                }
            }

            // V5 回放协议：轨迹必须恰好用尽，不足/多余都意味着记录与输入不匹配。
            if (reader.Position != reader.Count)
            {
                return ShuffleSynthesisResult.Failure(
                    ShuffleSynthesisErrorCode.TraceNotFullyConsumed,
                    reader.TraceLabel + " not fully consumed: consumed " +
                    reader.Position.ToString(CultureInfo.InvariantCulture) + " of " +
                    reader.Count.ToString(CultureInfo.InvariantCulture) + "; the record does not match this input.",
                    reader.Position,
                    n,
                    reader.Requests);
            }

            return ShuffleSynthesisResult.Success(
                RecordedRandomDraw.Create(recordId, wall), reader.Position, n, reader.Requests);
        }

        /// <summary>公开效用评估（供测试与工具复用；与 V4 evaluateTile 逐值一致）。</summary>
        public static double EvaluateTile(TileId candidateTile, ShuffleTarget target, IReadOnlyList<TileId> contextTiles)
        {
            switch (target)
            {
                case ShuffleTarget.Kokushi:
                {
                    // 国士无双：13 种幺九牌
                    if (!Contains(ShuffleTargets.KokushiIndices, candidateTile.TypeIndex))
                    {
                        return 0.0;
                    }

                    int count = CountType(contextTiles, candidateTile.TypeIndex);
                    switch (count)
                    {
                        case 0:
                            return 10.0; // 极力优先补齐缺失孤张
                        case 1:
                        {
                            bool pairExists = false;
                            foreach (int idx in ShuffleTargets.KokushiIndices)
                            {
                                if (CountType(contextTiles, idx) >= 2)
                                {
                                    pairExists = true;
                                    break;
                                }
                            }

                            return pairExists ? 1.0 : 5.0;
                        }
                        case 2:
                            return 0.2;
                        default:
                            return 0.0;
                    }
                }

                case ShuffleTarget.Daisangen:
                    if (candidateTile.Suit != TileSuit.Dragon)
                    {
                        return 0.0;
                    }

                    return HonorTripletUtility(candidateTile, contextTiles);

                case ShuffleTarget.Daisuushii:
                    if (candidateTile.Suit != TileSuit.Wind)
                    {
                        return 0.0;
                    }

                    return HonorTripletUtility(candidateTile, contextTiles);

                case ShuffleTarget.ChinitsuMan:
                    return EvaluateSuitUtility(candidateTile, TileSuit.Man, contextTiles);
                case ShuffleTarget.ChinitsuSou:
                    return EvaluateSuitUtility(candidateTile, TileSuit.Sou, contextTiles);
                case ShuffleTarget.ChinitsuPin:
                    return EvaluateSuitUtility(candidateTile, TileSuit.Pin, contextTiles);
                default:
                    throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown shuffle target.");
            }
        }

        private static double HonorTripletUtility(TileId tile, IReadOnlyList<TileId> contextTiles)
        {
            int count = CountType(contextTiles, tile.TypeIndex);
            switch (count)
            {
                case 0:
                    return 6.0;
                case 1:
                    return 8.0;
                case 2:
                    return 10.0;
                default:
                    return 1.0;
            }
        }

        /// <summary>清一色目标效用计算：花色张数 + 搭子临近度 + 雀头/刻子 + 赤宝牌。</summary>
        private static double EvaluateSuitUtility(TileId tile, TileSuit targetSuit, IReadOnlyList<TileId> contextTiles)
        {
            if (tile.Suit != targetSuit)
            {
                return 0.0;
            }

            double utility = 4.0;

            int sameRankCount = 0;
            int adj1Count = 0;
            int adj2Count = 0;
            for (int i = 0; i < contextTiles.Count; i++)
            {
                TileId other = contextTiles[i];
                if (other.Suit != targetSuit)
                {
                    continue;
                }

                int diff = other.Rank - tile.Rank;
                if (diff < 0)
                {
                    diff = -diff;
                }

                if (diff == 0)
                {
                    sameRankCount++;
                }
                else if (diff == 1)
                {
                    adj1Count++;
                }
                else if (diff == 2)
                {
                    adj2Count++;
                }
            }

            if (sameRankCount == 1)
            {
                utility += 2.5;
            }
            else if (sameRankCount == 2)
            {
                utility += 3.5;
            }

            if (adj1Count > 0)
            {
                utility += 2.0;
            }
            else if (adj2Count > 0)
            {
                utility += 1.0;
            }

            if (tile.IsRedFive)
            {
                utility += 0.5;
            }

            return utility;
        }

        /// <summary>
        /// 有界采样优化单个槽位：仅当存在 U(donor,H) > U(current,H) 的正收益供体时按 Δ² 抽样交换。
        /// 返回是否发生交换。
        /// </summary>
        private static bool ImproveSlot(
            TileId[] wall,
            int targetSlot,
            int seat,
            ShuffleTarget target,
            IReadOnlyList<TileId> contextTiles,
            HashSet<int> committedSlots,
            Dictionary<int, int> targetOwners,
            Dictionary<int, SeatShufflePlan> planBySeat,
            Dictionary<int, int[]> initialSlotsMap,
            Dictionary<int, int[]> drawSlotsMap,
            IRandomDecisionSource reader)
        {
            TileId currentTile = wall[targetSlot];
            double currentUtil = EvaluateTile(currentTile, target, contextTiles);

            var candidateSlots = new List<int>();
            var candidateDeltas = new List<double>();

            // 仅在活牌山 0..121 内挑选供体，绝不触碰王牌区 122..135。
            // 本人尚待处理的目标槽不可借用；其他施术者的尚待目标槽仅在换入牌不降低其目标效用时可借用。
            for (int d = 0; d < ShuffleSlots.MainWallTiles; d++)
            {
                if (d == targetSlot || committedSlots.Contains(d))
                {
                    continue;
                }

                if (targetOwners.TryGetValue(d, out int owner))
                {
                    if (owner == seat)
                    {
                        continue;
                    }

                    if (!planBySeat.TryGetValue(owner, out SeatShufflePlan? otherPlan) ||
                        !otherPlan.Selection.Target.HasValue)
                    {
                        continue;
                    }

                    ShuffleTarget otherTarget = otherPlan.Selection.Target.Value;
                    List<TileId> otherContext = ContextForSlot(
                        wall,
                        d,
                        initialSlotsMap[owner],
                        drawSlotsMap[owner],
                        otherTarget);
                    double donorForOther = EvaluateTile(wall[d], otherTarget, otherContext);
                    double incomingForOther = EvaluateTile(currentTile, otherTarget, otherContext);
                    if (incomingForOther < donorForOther)
                    {
                        continue;
                    }
                }

                double donorUtil = EvaluateTile(wall[d], target, contextTiles);
                double delta = donorUtil - currentUtil;
                if (delta > 0.0)
                {
                    candidateSlots.Add(d);
                    candidateDeltas.Add(delta);
                }
            }

            if (candidateSlots.Count == 0)
            {
                return false;
            }

            // 正收益平方加权抽样
            double totalWeight = 0.0;
            for (int i = 0; i < candidateDeltas.Count; i++)
            {
                totalWeight += candidateDeltas[i] * candidateDeltas[i];
            }

            if (totalWeight <= 0.0)
            {
                return false;
            }

            double pick = reader.NextDouble() * totalWeight;
            int chosen = candidateSlots[0];
            for (int i = 0; i < candidateSlots.Count; i++)
            {
                double delta = candidateDeltas[i];
                double w = delta * delta;
                pick -= w;
                if (pick <= 0.0)
                {
                    chosen = candidateSlots[i];
                    break;
                }
            }

            // 物理槽位原地对调（保持 136 张守恒、copyIndex 与赤五身份）
            TileId temp = wall[targetSlot];
            wall[targetSlot] = wall[chosen];
            wall[chosen] = temp;
            return true;
        }

        /// <summary>
        /// 摸牌段确定性虚拟手牌（约 13 张）：以本席位起手前 13 张为底，按自然顺位逐次
        /// “摸入”目标槽之前的牌，每次确定性打出目标效用最低的牌（并列按 typeIndex/copyIndex）。
        /// </summary>
        private static List<TileId> DrawVirtualHand(
            TileId[] wall,
            int slot,
            int[] initialSlots,
            int[] drawSlots,
            ShuffleTarget target)
        {
            var hand = new List<TileId>(14);

            // 统一取 13 张底牌：庄家的第 14 张（槽 52）不进入虚拟手牌，保持四席一致。
            for (int i = 0; i < initialSlots.Length && i < 13; i++)
            {
                hand.Add(wall[initialSlots[i]]);
            }

            int[] orderedDrawSlots = (int[])drawSlots.Clone();
            Array.Sort(orderedDrawSlots);
            foreach (int d in orderedDrawSlots)
            {
                if (d >= slot)
                {
                    break;
                }

                hand.Add(wall[d]);
                DiscardLowestUtility(hand, target);
            }

            return hand;
        }

        /// <summary>计算某施术者在指定目标槽被评估时的上下文手牌。</summary>
        private static List<TileId> ContextForSlot(
            TileId[] wall,
            int slot,
            int[] initialSlots,
            int[] drawSlots,
            ShuffleTarget target)
        {
            for (int i = 0; i < initialSlots.Length; i++)
            {
                if (initialSlots[i] == slot)
                {
                    var context = new List<TileId>(initialSlots.Length - 1);
                    for (int j = 0; j < initialSlots.Length; j++)
                    {
                        if (j != i)
                        {
                            context.Add(wall[initialSlots[j]]);
                        }
                    }

                    return context;
                }
            }

            return DrawVirtualHand(wall, slot, initialSlots, drawSlots, target);
        }

        private static void DiscardLowestUtility(List<TileId> hand, ShuffleTarget target)
        {
            if (hand.Count <= 1)
            {
                return;
            }

            int best = 0;
            double bestUtility = EvaluateTile(hand[0], target, hand);
            for (int i = 1; i < hand.Count; i++)
            {
                double utility = EvaluateTile(hand[i], target, hand);
                if (utility < bestUtility)
                {
                    bestUtility = utility;
                    best = i;
                }
                else if (utility == bestUtility)
                {
                    TileId candidate = hand[i];
                    TileId current = hand[best];
                    if (candidate.TypeIndex < current.TypeIndex ||
                        (candidate.TypeIndex == current.TypeIndex && candidate.CopyIndex < current.CopyIndex))
                    {
                        best = i;
                    }
                }
            }

            hand.RemoveAt(best);
        }

        private static Dictionary<int, double> ComputeEffectiveStrengths(
            List<SeatShufflePlan> plans,
            Dictionary<int, ShuffleTier> baseStrengthBySeat,
            double lambda,
            double diversity)
        {
            var profiles = new Dictionary<int, double[]>(plans.Count);
            foreach (SeatShufflePlan plan in plans)
            {
                profiles[plan.Seat] = ShuffleTargets.Demand(plan.Selection.Target!.Value);
            }

            var strengths = new Dictionary<int, double>(plans.Count);
            foreach (SeatShufflePlan plan in plans)
            {
                double[] myProfile = profiles[plan.Seat];
                double ownStrength = baseStrengthBySeat[plan.Seat].Strength;
                double interference = 0.0;
                double divergence = 0.0;
                foreach (SeatShufflePlan other in plans)
                {
                    if (other.Seat != plan.Seat)
                    {
                        double[] otherProfile = profiles[other.Seat];
                        double otherStrength = baseStrengthBySeat[other.Seat].Strength;
                        double similarity = ShuffleTargets.CosineSimilarity(myProfile, otherProfile);
                        interference += otherStrength * similarity;
                        divergence += otherStrength * (1.0 - similarity);
                    }
                }

                double denominator = 1.0 + (lambda * interference) + (diversity * divergence);
                double p = ownStrength / denominator;
                strengths[plan.Seat] = ShuffleTargets.Clamp01(p);
            }

            return strengths;
        }

        /// <summary>
        /// 计算各席位的成功交换额度缩放系数：scale_i = 1 / (1 + penalty * Σ_{j≠i}(1 - cos(d_i,d_j)))。
        /// 同目标时所有余弦为 1，缩放为 1。
        /// </summary>
        private static Dictionary<int, double> ComputeBudgetScales(List<SeatShufflePlan> plans, double penalty)
        {
            var scales = new Dictionary<int, double>(plans.Count);
            if (penalty <= 0.0)
            {
                foreach (SeatShufflePlan plan in plans)
                {
                    scales[plan.Seat] = 1.0;
                }

                return scales;
            }

            var profiles = new Dictionary<int, double[]>(plans.Count);
            foreach (SeatShufflePlan plan in plans)
            {
                profiles[plan.Seat] = ShuffleTargets.Demand(plan.Selection.Target!.Value);
            }

            foreach (SeatShufflePlan plan in plans)
            {
                double[] myProfile = profiles[plan.Seat];
                double divergence = 0.0;
                foreach (SeatShufflePlan other in plans)
                {
                    if (other.Seat == plan.Seat)
                    {
                        continue;
                    }

                    divergence += 1.0 - ShuffleTargets.CosineSimilarity(myProfile, profiles[other.Seat]);
                }

                scales[plan.Seat] = 1.0 / (1.0 + (penalty * divergence));
            }

            return scales;
        }

        /// <summary>
        /// V5 洗牌协议：Fisher-Yates，长度 m 的序列消耗 m-1 个有界整数决策（m &lt;= 1 消耗 0 个）。
        /// typed 入口直接使用 nextInt(i + 1) 的整数结果；legacy 入口在
        /// <see cref="DoubleTraceReader.NextInt"/> 内保持原有的 floor(double * (i + 1)) 语义。
        /// </summary>
        private static void ShuffledInPlace(int[] items, IRandomDecisionSource reader)
        {
            for (int i = items.Length - 1; i >= 1; i--)
            {
                int j = reader.NextInt(i + 1);
                int temp = items[i];
                items[i] = items[j];
                items[j] = temp;
            }
        }

        private static int CountDistinctTargets(List<SeatShufflePlan> plans)
        {
            var seen = new HashSet<ShuffleTarget>();
            foreach (SeatShufflePlan plan in plans)
            {
                seen.Add(plan.Selection.Target!.Value);
            }

            return seen.Count;
        }

        private static int[] CopySlots(IReadOnlyList<int> slots)
        {
            var copy = new int[slots.Count];
            for (int i = 0; i < slots.Count; i++)
            {
                copy[i] = slots[i];
            }

            return copy;
        }

        private static bool Contains(int[] values, int value)
        {
            foreach (int candidate in values)
            {
                if (candidate == value)
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountType(IReadOnlyList<TileId> tiles, int typeIndex)
        {
            int count = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i].TypeIndex == typeIndex)
                {
                    count++;
                }
            }

            return count;
        }

        private static ShuffleSynthesisResult Failure(
            ShuffleSynthesisErrorCode code,
            string message,
            int consumedRandomCount,
            int activePlanCount,
            IReadOnlyList<ShuffleTypedRandomDraw>? randomRequests = null)
        {
            return ShuffleSynthesisResult.Failure(code, message, consumedRandomCount, activePlanCount, randomRequests);
        }

        /// <summary>轨迹耗尽的内部信号；在既有 double-only 入口边界转为结构化失败。</summary>
        private sealed class TraceExhaustedSignal : Exception
        {
            public TraceExhaustedSignal()
                : base("Recorded random trace exhausted.")
            {
            }
        }

        /// <summary>
        /// 合成算法的唯一随机消费面。typed 与 legacy 入口只在“如何取得下一个值”上不同，
        /// 决策顺序、槽位/供体选择、预算与失败边界全部共享同一实现。
        /// </summary>
        private interface IRandomDecisionSource
        {
            /// <summary>记录中的事件/值总数。</summary>
            int Count { get; }

            /// <summary>已经成功匹配并应用的事件/值数量。</summary>
            int Position { get; }

            /// <summary>错误消息中使用的记录名称（legacy: recorded doubles；typed: typed random draws）。</summary>
            string TraceLabel { get; }

            /// <summary>已应用请求的只读快照。</summary>
            IReadOnlyList<ShuffleTypedRandomDraw> Requests { get; }

            int NextInt(int bound);

            double NextDouble();
        }

        /// <summary>
        /// 既有 double-only 记录适配器：保持原语义，以 floor(pick * bound) 实现有界选择；
        /// 同时把每次实际请求记录为 typed draw，供兼容性测试核对。
        /// </summary>
        private sealed class DoubleTraceReader : IRandomDecisionSource
        {
            private readonly IReadOnlyList<double> _values;
            private readonly List<ShuffleTypedRandomDraw> _requests = new List<ShuffleTypedRandomDraw>();
            private int _index;

            internal DoubleTraceReader(IReadOnlyList<double> values)
            {
                _values = values;
            }

            public int Count => _values.Count;

            public int Position => _index;

            public string TraceLabel => "recorded doubles";

            public IReadOnlyList<ShuffleTypedRandomDraw> Requests => _requests;

            public int NextInt(int bound)
            {
                if (bound <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(bound), bound, "nextInt bound must be positive.");
                }

                double pick = ReadRaw();
                int result = (int)(pick * bound);
                if (result < 0)
                {
                    result = 0;
                }
                else if (result >= bound)
                {
                    result = bound - 1;
                }

                _requests.Add(ShuffleTypedRandomDraw.NextInt(bound, result));
                return result;
            }

            public double NextDouble()
            {
                double value = ReadRaw();
                _requests.Add(ShuffleTypedRandomDraw.NextDouble(value));
                return value;
            }

            private double ReadRaw()
            {
                if (_index >= _values.Count)
                {
                    throw new TraceExhaustedSignal();
                }

                double value = _values[_index];
                _index++;
                return value;
            }
        }

        /// <summary>
        /// 有序 typed 记录读取器：严格按位置消费，kind/bound 必须与算法请求完全一致；
        /// 不搜索、不跳过、不重排。失败事件不计入 Position（即“已消费”数量）。
        /// </summary>
        private sealed class TypedTraceReader : IRandomDecisionSource
        {
            private readonly ShuffleTypedRandomTrace _trace;
            private readonly List<ShuffleTypedRandomDraw> _requests = new List<ShuffleTypedRandomDraw>();
            private int _index;

            internal TypedTraceReader(ShuffleTypedRandomTrace trace)
            {
                _trace = trace;
            }

            public int Count => _trace.Count;

            public int Position => _index;

            public string TraceLabel => "typed random draws";

            public IReadOnlyList<ShuffleTypedRandomDraw> Requests => _requests;

            public int NextInt(int bound)
            {
                if (_index >= _trace.Count)
                {
                    throw new TypedTraceExhaustedSignal();
                }

                ShuffleTypedRandomDraw draw = _trace.Draws[_index];
                if (draw.Kind != ShuffleTypedRandomDrawKind.NextInt)
                {
                    throw new TypedTraceMismatchSignal(
                        ShuffleSynthesisErrorCode.UnexpectedTypedDrawKind,
                        "typed draw at index " + _index.ToString(CultureInfo.InvariantCulture) +
                        " must be nextInt(bound=" + bound.ToString(CultureInfo.InvariantCulture) +
                        ") but was " + draw + ".");
                }

                if (draw.Bound != bound)
                {
                    throw new TypedTraceMismatchSignal(
                        ShuffleSynthesisErrorCode.TypedDrawBoundMismatch,
                        "typed draw at index " + _index.ToString(CultureInfo.InvariantCulture) +
                        " nextInt bound mismatch: expected " + bound.ToString(CultureInfo.InvariantCulture) +
                        ", got " + draw.Bound.ToString(CultureInfo.InvariantCulture) +
                        " (result=" + draw.Result.ToString(CultureInfo.InvariantCulture) + ").");
                }

                _index++;
                _requests.Add(draw);
                return draw.Result;
            }

            public double NextDouble()
            {
                if (_index >= _trace.Count)
                {
                    throw new TypedTraceExhaustedSignal();
                }

                ShuffleTypedRandomDraw draw = _trace.Draws[_index];
                if (draw.Kind != ShuffleTypedRandomDrawKind.NextDouble)
                {
                    throw new TypedTraceMismatchSignal(
                        ShuffleSynthesisErrorCode.UnexpectedTypedDrawKind,
                        "typed draw at index " + _index.ToString(CultureInfo.InvariantCulture) +
                        " must be nextDouble but was " + draw + ".");
                }

                _index++;
                _requests.Add(draw);
                return draw.Value;
            }
        }

        private sealed class TypedTraceExhaustedSignal : Exception
        {
            public TypedTraceExhaustedSignal()
                : base("Typed random trace exhausted.")
            {
            }
        }

        private sealed class TypedTraceMismatchSignal : Exception
        {
            public TypedTraceMismatchSignal(ShuffleSynthesisErrorCode code, string message)
                : base(message)
            {
                Code = code;
            }

            public ShuffleSynthesisErrorCode Code { get; }
        }
    }
}
