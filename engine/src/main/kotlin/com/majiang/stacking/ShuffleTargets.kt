package com.majiang.stacking

import kotlin.math.sqrt

/**
 * 洗牌做牌目标 34 维需求特征空间与相似度计算器
 *
 * 34 维 typeIndex 映射顺序（与 Tile.typeIndex 一致）：
 * - 0..8: 万子 1..9 万
 * - 9..17: 索子 1..9 索
 * - 18..26: 筒子 1..9 筒
 * - 27..30: 风牌 東, 南, 西, 北
 * - 31..33: 三元牌 白, 發, 中
 */
object ShuffleTargets {
    const val DIMENSION = 34

    // 国士无双 13 种幺九牌 typeIndex 集合
    val KOKUSHI_INDICES: List<Int> = listOf(
        0, 8,           // 1万, 9万
        9, 17,          // 1索, 9索
        18, 26,         // 1筒, 9筒
        27, 28, 29, 30, // 東, 南, 西, 北
        31, 32, 33      // 白, 發, 中
    )

    // 大三元 3 种三元牌 typeIndex 集合
    val DAISANGEN_INDICES: List<Int> = listOf(31, 32, 33)

    // 大四喜 4 种风牌 typeIndex 集合
    val DAISUUSHII_INDICES: List<Int> = listOf(27, 28, 29, 30)

    /**
     * 获取单一做牌目标的 34 维需求向量（L1 归一化，所有元素之和为 1.0）
     * 每次调用返回全新防御性副本
     */
    fun demand(target: ShuffleTarget): DoubleArray {
        val vec = DoubleArray(DIMENSION)
        when (target) {
            ShuffleTarget.CHINITSU_MAN -> {
                val weight = 1.0 / 9.0
                for (i in 0..8) vec[i] = weight
            }
            ShuffleTarget.CHINITSU_SOU -> {
                val weight = 1.0 / 9.0
                for (i in 9..17) vec[i] = weight
            }
            ShuffleTarget.CHINITSU_PIN -> {
                val weight = 1.0 / 9.0
                for (i in 18..26) vec[i] = weight
            }
            ShuffleTarget.DAISANGEN -> {
                val weight = 1.0 / 3.0
                for (i in DAISANGEN_INDICES) vec[i] = weight
            }
            ShuffleTarget.KOKUSHI -> {
                val weight = 1.0 / 13.0
                for (i in KOKUSHI_INDICES) vec[i] = weight
            }
            ShuffleTarget.DAISUUSHII -> {
                val weight = 1.0 / 4.0
                for (i in DAISUUSHII_INDICES) vec[i] = weight
            }
        }
        return vec
    }

    /**
     * 计算两个做牌目标之间的资源重叠冲突系数 [0.0, 1.0]（余弦相似度）
     */
    fun overlap(a: ShuffleTarget, b: ShuffleTarget): Double {
        if (a == b) return 1.0
        return cosineSimilarity(demand(a), demand(b))
    }

    /**
     * 多目标等权合成并归一化；若 targets 为空则返回全 0 向量
     * 每次调用返回全新防御性副本
     */
    fun combined(targets: Set<ShuffleTarget>): DoubleArray {
        val result = DoubleArray(DIMENSION)
        if (targets.isEmpty()) return result

        for (target in targets) {
            val d = demand(target)
            for (i in 0 until DIMENSION) {
                result[i] += d[i]
            }
        }

        val total = result.sum()
        if (total > 0.0) {
            for (i in 0 until DIMENSION) {
                result[i] /= total
            }
        }
        return result
    }

    /**
     * 计算两组 34 维需求向量之间的余弦相似度 [0.0, 1.0]
     */
    fun cosineSimilarity(v1: DoubleArray, v2: DoubleArray): Double {
        require(v1.size == DIMENSION && v2.size == DIMENSION) {
            "Vectors must have dimension $DIMENSION, got ${v1.size} and ${v2.size}"
        }
        var dot = 0.0
        var normSq1 = 0.0
        var normSq2 = 0.0
        for (i in 0 until DIMENSION) {
            val a = v1[i]
            val b = v2[i]
            dot += a * b
            normSq1 += a * a
            normSq2 += b * b
        }
        if (normSq1 <= 0.0 || normSq2 <= 0.0) return 0.0
        val normProduct = sqrt(normSq1) * sqrt(normSq2)
        if (normProduct <= 0.0) return 0.0
        val sim = dot / normProduct
        return sim.coerceIn(0.0, 1.0)
    }
}
