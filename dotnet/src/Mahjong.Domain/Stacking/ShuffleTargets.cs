using System;
using System.Collections.Generic;

namespace Mahjong.Domain.Stacking
{
    /// <summary>
    /// 洗牌做牌目标 34 维需求特征空间与相似度计算器
    /// （V4 <c>com.majiang.stacking.ShuffleTargets</c> 的逐值移植）。
    ///
    /// 34 维 typeIndex 映射顺序（与 TileId.TypeIndex 一致）：
    /// - 0..8: 万子 1..9 万；9..17: 索子 1..9 索；18..26: 筒子 1..9 筒；
    /// - 27..30: 风牌 東南西北；31..33: 三元牌 白發中。
    /// 纯数学函数：无状态、无副作用，跨运行时逐位一致（IEEE 754 双精度）。
    /// </summary>
    public static class ShuffleTargets
    {
        public const int Dimension = 34;

        /// <summary>国士无双 13 种幺九牌 typeIndex 集合。</summary>
        public static readonly int[] KokushiIndices =
        {
            0, 8,            // 1万, 9万
            9, 17,           // 1索, 9索
            18, 26,          // 1筒, 9筒
            27, 28, 29, 30,  // 東, 南, 西, 北
            31, 32, 33       // 白, 發, 中
        };

        /// <summary>大三元 3 种三元牌 typeIndex 集合。</summary>
        public static readonly int[] DaisangenIndices = { 31, 32, 33 };

        /// <summary>大四喜 4 种风牌 typeIndex 集合。</summary>
        public static readonly int[] DaisuushiiIndices = { 27, 28, 29, 30 };

        /// <summary>
        /// 获取单一做牌目标的 34 维需求向量（L1 归一化，所有元素之和为 1.0）。
        /// 每次调用返回全新防御性副本。非法枚举值抛出 ArgumentOutOfRangeException。
        /// </summary>
        public static double[] Demand(ShuffleTarget target)
        {
            ShuffleTargetRules.RequireDefined(target);
            var vec = new double[Dimension];
            switch (target)
            {
                case ShuffleTarget.ChinitsuMan:
                    FillRange(vec, 0, 8, 1.0 / 9.0);
                    break;
                case ShuffleTarget.ChinitsuSou:
                    FillRange(vec, 9, 17, 1.0 / 9.0);
                    break;
                case ShuffleTarget.ChinitsuPin:
                    FillRange(vec, 18, 26, 1.0 / 9.0);
                    break;
                case ShuffleTarget.Daisangen:
                    foreach (int i in DaisangenIndices)
                    {
                        vec[i] = 1.0 / 3.0;
                    }

                    break;
                case ShuffleTarget.Kokushi:
                    foreach (int i in KokushiIndices)
                    {
                        vec[i] = 1.0 / 13.0;
                    }

                    break;
                case ShuffleTarget.Daisuushii:
                    foreach (int i in DaisuushiiIndices)
                    {
                        vec[i] = 1.0 / 4.0;
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown shuffle target.");
            }

            return vec;
        }

        /// <summary>计算两个做牌目标之间的资源重叠冲突系数 [0.0, 1.0]（余弦相似度）。</summary>
        public static double Overlap(ShuffleTarget a, ShuffleTarget b)
        {
            if (a == b)
            {
                return 1.0;
            }

            return CosineSimilarity(Demand(a), Demand(b));
        }

        /// <summary>
        /// 多目标等权合成并归一化；targets 为空时返回全 0 向量。
        /// 每次调用返回全新防御性副本。
        /// </summary>
        public static double[] Combined(IReadOnlyCollection<ShuffleTarget> targets)
        {
            if (targets == null)
            {
                throw new ArgumentNullException(nameof(targets));
            }

            var result = new double[Dimension];
            if (targets.Count == 0)
            {
                return result;
            }

            foreach (ShuffleTarget target in targets)
            {
                double[] d = Demand(target);
                for (int i = 0; i < Dimension; i++)
                {
                    result[i] += d[i];
                }
            }

            double total = 0.0;
            for (int i = 0; i < Dimension; i++)
            {
                total += result[i];
            }

            if (total > 0.0)
            {
                for (int i = 0; i < Dimension; i++)
                {
                    result[i] /= total;
                }
            }

            return result;
        }

        /// <summary>计算两组 34 维需求向量之间的余弦相似度 [0.0, 1.0]。</summary>
        public static double CosineSimilarity(double[] v1, double[] v2)
        {
            if (v1 == null)
            {
                throw new ArgumentNullException(nameof(v1));
            }

            if (v2 == null)
            {
                throw new ArgumentNullException(nameof(v2));
            }

            if (v1.Length != Dimension || v2.Length != Dimension)
            {
                throw new ArgumentException(
                    "Vectors must have dimension " + Dimension.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ", got " + v1.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " and " +
                    v2.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
            }

            double dot = 0.0;
            double normSq1 = 0.0;
            double normSq2 = 0.0;
            for (int i = 0; i < Dimension; i++)
            {
                double a = v1[i];
                double b = v2[i];
                dot += a * b;
                normSq1 += a * a;
                normSq2 += b * b;
            }

            if (normSq1 <= 0.0 || normSq2 <= 0.0)
            {
                return 0.0;
            }

            double normProduct = Math.Sqrt(normSq1) * Math.Sqrt(normSq2);
            if (normProduct <= 0.0)
            {
                return 0.0;
            }

            double sim = dot / normProduct;
            return Clamp01(sim);
        }

        private static void FillRange(double[] vec, int fromInclusive, int toInclusive, double weight)
        {
            for (int i = fromInclusive; i <= toInclusive; i++)
            {
                vec[i] = weight;
            }
        }

        internal static double Clamp01(double value)
        {
            if (value < 0.0)
            {
                return 0.0;
            }

            if (value > 1.0)
            {
                return 1.0;
            }

            return value;
        }
    }
}
