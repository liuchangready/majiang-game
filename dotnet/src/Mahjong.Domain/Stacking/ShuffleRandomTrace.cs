using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mahjong.Domain.Stacking
{
    /// <summary>
    /// 码牌阶段明确记录的随机输入（V5 协议；取代 V4 的 seed 权威随机源）。
    ///
    /// V4 用 <c>Random(seed)</c> 在 JVM 内部派生全部随机决策，这不是跨运行时可重放规范
    /// （开放产品决策 D-09），因此 V5 Domain 不接收 seed、不创建 RNG，而是把每个随机决策
    /// 的结果作为显式输入记录：
    /// - <see cref="BaseWall"/>：基础洗牌结果（136 张唯一实体牌的完整牌序）；
    /// - <see cref="Doubles"/>：后续随机决策按固定顺序消耗的 [0,1) 双精度值序列，
    ///   依次覆盖：席位/槽位洗牌（Fisher-Yates，长度 m 消耗 m-1 个）、
    ///   小数额度最后一搏判定、熟练度判定、供体 Delta^2 加权抽样。
    /// 相同的记录输入必须逐张确定性回放（<see cref="ShuffleWallSynthesizer"/> 严格校验
    /// 消耗数量：不足返回 TraceExhausted，多余返回 TraceNotFullyConsumed）。
    ///
    /// 本类型只封装与校验记录：Domain 不产生随机值、不读时钟、不生成 ID，
    /// 记录由宿主（权威层）在真实随机事件发生时写入。
    /// </summary>
    public sealed class ShuffleRandomTrace
    {
        private ShuffleRandomTrace(string recordId, RecordedRandomDraw baseWall, double[] doubles)
        {
            RecordId = recordId;
            BaseWall = baseWall;
            // 只读包装而非直接暴露数组：调用方无法通过 (double[])Doubles 强制转换
            // 改写创建后的记录（输入记录不可变是重复回放确定性的前提）。
            Doubles = new System.Collections.ObjectModel.ReadOnlyCollection<double>(doubles);
        }

        /// <summary>记录标识（宿主提供，通常为局 ID 派生）。</summary>
        public string RecordId { get; }

        /// <summary>记录的基础洗牌结果：136 张唯一实体牌。</summary>
        public RecordedRandomDraw BaseWall { get; }

        /// <summary>
        /// 记录的后续随机决策值序列（每个值 ∈ [0,1)）。
        /// 不可变只读视图：外部修改创建时的源数组不会影响本记录，
        /// 且该视图不可转换为可变数组。
        /// </summary>
        public IReadOnlyList<double> Doubles { get; }

        /// <summary>创建并校验记录；非法输入（重复/缺失牌、越界随机值）直接抛出。</summary>
        public static ShuffleRandomTrace Create(string recordId, IReadOnlyList<TileId> baseWallOrder, IReadOnlyList<double> doubles)
        {
            bool created = TryCreate(recordId, baseWallOrder, doubles, out ShuffleRandomTrace? trace, out string? error);
            if (!created)
            {
                throw new ArgumentException(error ?? "Invalid shuffle random trace.", nameof(baseWallOrder));
            }

            return trace!;
        }

        /// <summary>
        /// 校验并创建记录。返回 false 时 <paramref name="error"/> 给出具体原因，零副作用。
        /// </summary>
        public static bool TryCreate(
            string recordId,
            IReadOnlyList<TileId> baseWallOrder,
            IReadOnlyList<double> doubles,
            out ShuffleRandomTrace? trace,
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

            if (!ValidateBaseWall(baseWallOrder, out error))
            {
                return false;
            }

            if (doubles == null)
            {
                error = "doubles must not be null.";
                return false;
            }

            if (!ValidateDoubles(doubles, out error))
            {
                return false;
            }

            var wall = RecordedRandomDraw.Create(recordId, baseWallOrder);
            var copy = new double[doubles.Count];
            for (int i = 0; i < doubles.Count; i++)
            {
                copy[i] = doubles[i];
            }

            trace = new ShuffleRandomTrace(recordId, wall, copy);
            error = null;
            return true;
        }

        /// <summary>仅有基础洗牌记录、无后续随机决策值的空轨迹（无千术桌恰好消耗 0 个值）。</summary>
        public static ShuffleRandomTrace WithoutDraws(RecordedRandomDraw baseWall)
        {
            if (baseWall == null)
            {
                throw new ArgumentNullException(nameof(baseWall));
            }

            return new ShuffleRandomTrace(baseWall.RecordId, baseWall, new double[0]);
        }

        internal static bool ValidateBaseWall(IReadOnlyList<TileId> baseWallOrder, out string? error)
        {
            if (baseWallOrder.Count != TileId.Count)
            {
                error = "base wall must contain exactly " + TileId.Count.ToString(CultureInfo.InvariantCulture) +
                        " tiles, got " + baseWallOrder.Count.ToString(CultureInfo.InvariantCulture) + ".";
                return false;
            }

            var seen = new bool[TileId.Count];
            for (int i = 0; i < baseWallOrder.Count; i++)
            {
                int value = baseWallOrder[i].Value;
                if (value < TileId.MinValue || value > TileId.MaxValue)
                {
                    error = "base wall tile at index " + i.ToString(CultureInfo.InvariantCulture) +
                            " is out of range 0..135.";
                    return false;
                }

                if (seen[value])
                {
                    error = "base wall contains duplicate tile " + value.ToString(CultureInfo.InvariantCulture) + ".";
                    return false;
                }

                seen[value] = true;
            }

            error = null;
            return true;
        }

        internal static bool ValidateDoubles(IReadOnlyList<double> doubles, out string? error)
        {
            for (int i = 0; i < doubles.Count; i++)
            {
                double value = doubles[i];
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value >= 1.0)
                {
                    error = "recorded double at index " + i.ToString(CultureInfo.InvariantCulture) +
                            " must be in [0,1), got " + value.ToString("R", CultureInfo.InvariantCulture) + ".";
                    return false;
                }
            }

            error = null;
            return true;
        }
    }
}
