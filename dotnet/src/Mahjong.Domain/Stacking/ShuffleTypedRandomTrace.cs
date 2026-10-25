using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Mahjong.Domain.Stacking
{
    /// <summary>
    /// 有序 typed 随机事件的种类。值从 1 开始，使 <c>default</c>（0）不是合法种类，
    /// 便于在记录导入时明确拒绝未初始化/未知的 kind。
    /// </summary>
    public enum ShuffleTypedRandomDrawKind
    {
        /// <summary>精确的有界整数选择：<c>0 &lt;= Result &lt; Bound</c>。</summary>
        NextInt = 1,

        /// <summary>精确的 [0,1) 双精度选择。</summary>
        NextDouble = 2
    }

    /// <summary>
    /// typed 随机事件的原始输入记录（传输/导入形态）。
    ///
    /// 本类型只承载字段，不在此处做语义校验；<see cref="ShuffleTypedRandomTrace.TryCreate"/>
    /// 会在记录边界统一校验 kind/bound/result/value，并复制为不可变的
    /// <see cref="ShuffleTypedRandomDraw"/>。这样未知 kind（例如反序列化得到 0 或 99）、
    /// 非法 bound/result 与 NaN/Infinity 都能被明确拒绝，而不是在合成中途才暴露。
    ///
    /// 规范化约定：NextInt 事件不使用 <see cref="Value"/>，必须为 0.0；
    /// NextDouble 事件不使用 <see cref="Bound"/>/<see cref="Result"/>，必须为 0。
    /// </summary>
    public sealed class ShuffleTypedRandomEvent
    {
        public ShuffleTypedRandomEvent(
            ShuffleTypedRandomDrawKind kind,
            int bound,
            int result,
            double value)
        {
            Kind = kind;
            Bound = bound;
            Result = result;
            Value = value;
        }

        public ShuffleTypedRandomDrawKind Kind { get; }

        public int Bound { get; }

        public int Result { get; }

        public double Value { get; }

        /// <summary>构造一条 NextInt 原始事件；字段校验发生在 <see cref="ShuffleTypedRandomTrace.TryCreate"/>。</summary>
        public static ShuffleTypedRandomEvent ForNextInt(int bound, int result)
        {
            return new ShuffleTypedRandomEvent(ShuffleTypedRandomDrawKind.NextInt, bound, result, 0.0);
        }

        /// <summary>构造一条 NextDouble 原始事件；字段校验发生在 <see cref="ShuffleTypedRandomTrace.TryCreate"/>。</summary>
        public static ShuffleTypedRandomEvent ForNextDouble(double value)
        {
            return new ShuffleTypedRandomEvent(ShuffleTypedRandomDrawKind.NextDouble, 0, 0, value);
        }

        public override string ToString()
        {
            return Kind == ShuffleTypedRandomDrawKind.NextInt
                ? "nextInt(bound=" + Bound.ToString(CultureInfo.InvariantCulture) +
                  ", result=" + Result.ToString(CultureInfo.InvariantCulture) + ")"
                : "nextDouble(value=" + Value.ToString("R", CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>
    /// 已校验、不可变的 typed 随机决策。
    ///
    /// 只由 <see cref="ShuffleTypedRandomTrace"/> 创建：属性在创建后不可修改，
    /// NextInt 保证 <c>0 &lt; Bound &amp;&amp; 0 &lt;= Result &lt; Bound</c>，
    /// NextDouble 保证有限且 <c>0.0 &lt;= Value &lt; 1.0</c>。
    /// 未使用的字段固定为 0（NextInt.Value == 0.0，NextDouble.Bound/Result == 0），
    /// 使类型互换不会因“看起来像合法值”而被悄悄接受。
    /// </summary>
    public sealed class ShuffleTypedRandomDraw
    {
        private ShuffleTypedRandomDraw(
            ShuffleTypedRandomDrawKind kind,
            int bound,
            int result,
            double value)
        {
            Kind = kind;
            Bound = bound;
            Result = result;
            Value = value;
        }

        public ShuffleTypedRandomDrawKind Kind { get; }

        /// <summary>NextInt 的精确正上界；NextDouble 恒为 0。</summary>
        public int Bound { get; }

        /// <summary>NextInt 的结果，满足 0 &lt;= Result &lt; Bound；NextDouble 恒为 0。</summary>
        public int Result { get; }

        /// <summary>NextDouble 的值，满足有限且 [0,1)；NextInt 恒为 0.0。</summary>
        public double Value { get; }

        internal static ShuffleTypedRandomDraw NextInt(int bound, int result)
        {
            if (bound <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bound), bound, "nextInt bound must be positive.");
            }

            if (result < 0 || result >= bound)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(result),
                    result,
                    "nextInt result must be in 0..bound-1 with bound=" + bound.ToString(CultureInfo.InvariantCulture) + ".");
            }

            return new ShuffleTypedRandomDraw(ShuffleTypedRandomDrawKind.NextInt, bound, result, 0.0);
        }

        internal static ShuffleTypedRandomDraw NextDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value >= 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "nextDouble value must be finite and in [0,1).");
            }

            return new ShuffleTypedRandomDraw(ShuffleTypedRandomDrawKind.NextDouble, 0, 0, value);
        }

        public override string ToString()
        {
            return Kind == ShuffleTypedRandomDrawKind.NextInt
                ? "nextInt(bound=" + Bound.ToString(CultureInfo.InvariantCulture) +
                  ", result=" + Result.ToString(CultureInfo.InvariantCulture) + ")"
                : "nextDouble(value=" + Value.ToString("R", CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>
    /// 码牌阶段基础墙形成之后的有序 typed 随机输入记录（V5 P1.5 / D-R1-02 seam）。
    ///
    /// 现有 <see cref="ShuffleRandomTrace"/> 只记录 [0,1) doubles，并把 Fisher-Yates 选择
    /// 表示为 <c>floor(double * (i + 1))</c>。V4 的真实决策并非全部 doubles：
    /// 席位/槽位洗牌使用 Kotlin <c>shuffled(rng)</c> 的 <c>nextInt(i + 1)</c>，
    /// 小数额度最后一搏、熟练度判定和供体 Delta^2 抽样才使用 <c>nextDouble()</c>。
    /// 仅记录 doubles 无法与 V4 的有序 int/double 调用一一对应，因此本类型记录：
    /// - <see cref="BaseWall"/>：基础洗牌结果（136 张唯一实体牌；基础洗牌本身仍作为记录输入，
    ///   不复刻 V4 seed/RNG，不改变 rulesetVersion）；
    /// - <see cref="Draws"/>：基础墙形成后按真实调用顺序记录的全部 typed 事件。
    ///
    /// 本记录严格保持输入顺序：合成器按顺序消费，绝不排序、搜索、跳过或重排事件。
    /// 创建时复制基础墙与事件输入，对外只暴露只读视图；Domain 不创建随机值、不读时钟、
    /// 不生成 ID。字段/基础墙非法时明确拒绝，且不产生部分记录。
    /// </summary>
    public sealed class ShuffleTypedRandomTrace
    {
        private readonly ShuffleTypedRandomDraw[] _draws;
        private readonly ReadOnlyCollection<ShuffleTypedRandomDraw> _readOnlyDraws;

        private ShuffleTypedRandomTrace(
            string recordId,
            RecordedRandomDraw baseWall,
            ShuffleTypedRandomDraw[] draws)
        {
            RecordId = recordId;
            BaseWall = baseWall;
            _draws = draws;
            // 只读包装而非暴露内部数组：调用方无法把 Draws 强转为可变数组改写记录。
            _readOnlyDraws = Array.AsReadOnly(_draws);
        }

        /// <summary>记录标识（宿主提供，通常为局 ID 派生）。</summary>
        public string RecordId { get; }

        /// <summary>基础洗牌结果：136 张唯一实体牌；本对象持有创建时的独立副本。</summary>
        public RecordedRandomDraw BaseWall { get; }

        /// <summary>按真实调用顺序记录的 typed 事件；不可变只读视图。</summary>
        public IReadOnlyList<ShuffleTypedRandomDraw> Draws => _readOnlyDraws;

        /// <summary>事件数量（不是牌数）。</summary>
        public int Count => _draws.Length;

        /// <summary>创建并校验记录；非法输入直接抛出 <see cref="ArgumentException"/>。</summary>
        public static ShuffleTypedRandomTrace Create(
            string recordId,
            IReadOnlyList<TileId> baseWallOrder,
            IReadOnlyList<ShuffleTypedRandomEvent> draws)
        {
            bool created = TryCreate(recordId, baseWallOrder, draws, out ShuffleTypedRandomTrace? trace, out string? error);
            if (!created)
            {
                throw new ArgumentException(error ?? "Invalid typed shuffle random trace.");
            }

            return trace!;
        }

        /// <summary>
        /// 校验并创建记录。返回 false 时 <paramref name="error"/> 给出具体原因，零副作用，
        /// 且不会返回部分记录。校验内容：recordId 非空、基础墙恰为 136 张 0..135 唯一实体牌、
        /// 事件列表非 null 且逐条满足 kind/bound/result/value 契约（空列表合法，表示无后续决策）。
        /// </summary>
        public static bool TryCreate(
            string recordId,
            IReadOnlyList<TileId> baseWallOrder,
            IReadOnlyList<ShuffleTypedRandomEvent> draws,
            out ShuffleTypedRandomTrace? trace,
            out string? error)
        {
            trace = null;
            if (string.IsNullOrEmpty(recordId))
            {
                error = "recordId must be non-empty.";
                return false;
            }

            if (baseWallOrder == null)
            {
                error = "baseWallOrder must not be null.";
                return false;
            }

            // 复用既有记录的基础墙校验：136 张、值域 0..135、无重复（因此也无缺失）。
            if (!ShuffleRandomTrace.ValidateBaseWall(baseWallOrder, out error))
            {
                return false;
            }

            if (draws == null)
            {
                error = "typed draws must not be null.";
                return false;
            }

            var validated = new ShuffleTypedRandomDraw[draws.Count];
            for (int i = 0; i < draws.Count; i++)
            {
                ShuffleTypedRandomEvent? input = draws[i];
                if (input == null)
                {
                    error = "typed draw at index " + i.ToString(CultureInfo.InvariantCulture) + " must not be null.";
                    return false;
                }

                if (!TryValidateDraw(input, i, out ShuffleTypedRandomDraw? draw, out error))
                {
                    return false;
                }

                validated[i] = draw!;
            }

            // 独立复制基础墙（RecordedRandomDraw.Create 复制并再次校验 136 张物理身份）。
            RecordedRandomDraw wall = RecordedRandomDraw.Create(recordId, baseWallOrder);
            trace = new ShuffleTypedRandomTrace(recordId, wall, validated);
            error = null;
            return true;
        }

        /// <summary>仅有基础洗牌记录、无后续 typed 事件（无千术桌的合法空轨迹）。</summary>
        public static ShuffleTypedRandomTrace WithoutDraws(RecordedRandomDraw baseWall)
        {
            if (baseWall == null)
            {
                throw new ArgumentNullException(nameof(baseWall));
            }

            // 复制而不是复用传入对象，保证记录创建后外部无法通过原对象影响本记录。
            RecordedRandomDraw copy = RecordedRandomDraw.Create(baseWall.RecordId, baseWall.WallOrder);
            return new ShuffleTypedRandomTrace(baseWall.RecordId, copy, Array.Empty<ShuffleTypedRandomDraw>());
        }

        private static bool TryValidateDraw(
            ShuffleTypedRandomEvent input,
            int index,
            out ShuffleTypedRandomDraw? draw,
            out string? error)
        {
            draw = null;
            switch (input.Kind)
            {
                case ShuffleTypedRandomDrawKind.NextInt:
                {
                    if (input.Bound <= 0)
                    {
                        error = "typed draw at index " + index.ToString(CultureInfo.InvariantCulture) +
                                " nextInt bound must be positive, got " +
                                input.Bound.ToString(CultureInfo.InvariantCulture) + ".";
                        return false;
                    }

                    if (input.Result < 0 || input.Result >= input.Bound)
                    {
                        error = "typed draw at index " + index.ToString(CultureInfo.InvariantCulture) +
                                " nextInt result must be in 0..bound-1 with bound=" +
                                input.Bound.ToString(CultureInfo.InvariantCulture) + ", got " +
                                input.Result.ToString(CultureInfo.InvariantCulture) + ".";
                        return false;
                    }

                    if (input.Value != 0.0)
                    {
                        error = "typed draw at index " + index.ToString(CultureInfo.InvariantCulture) +
                                " is nextInt and must carry value 0.0, got " +
                                input.Value.ToString("R", CultureInfo.InvariantCulture) + ".";
                        return false;
                    }

                    draw = ShuffleTypedRandomDraw.NextInt(input.Bound, input.Result);
                    error = null;
                    return true;
                }

                case ShuffleTypedRandomDrawKind.NextDouble:
                {
                    if (input.Bound != 0 || input.Result != 0)
                    {
                        error = "typed draw at index " + index.ToString(CultureInfo.InvariantCulture) +
                                " is nextDouble and must carry bound/result 0, got bound=" +
                                input.Bound.ToString(CultureInfo.InvariantCulture) + ", result=" +
                                input.Result.ToString(CultureInfo.InvariantCulture) + ".";
                        return false;
                    }

                    double value = input.Value;
                    if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value >= 1.0)
                    {
                        error = "typed draw at index " + index.ToString(CultureInfo.InvariantCulture) +
                                " nextDouble value must be finite and in [0,1), got " +
                                value.ToString("R", CultureInfo.InvariantCulture) + ".";
                        return false;
                    }

                    draw = ShuffleTypedRandomDraw.NextDouble(value);
                    error = null;
                    return true;
                }

                default:
                {
                    error = "typed draw at index " + index.ToString(CultureInfo.InvariantCulture) +
                            " has unknown kind " + ((int)input.Kind).ToString(CultureInfo.InvariantCulture) + ".";
                    return false;
                }
            }
        }
    }
}
