package com.majiang.rules

import com.majiang.model.*

/**
 * 手牌分解器 — 核心算法组件
 *
 * 将 14 张牌拆解为所有可能的合法组合：
 * - 标准形：4 面子（顺子/刻子） + 1 雀头
 * - 七对子：7 组对子
 * - 国士无双：13 种幺九各一张 + 其中一种成对
 *
 * 算法采用递归回溯法，在 34 维牌种计数数组上高效搜索。
 */
class HandDecomposer {

    /**
     * 面子分解结果
     * @property mentsu 面子列表（每组用 typeIndex 列表表示）
     * @property jantai 雀头的 typeIndex
     * @property pattern 分解模式
     */
    data class Decomposition(
        val mentsu: List<MentsuGroup>,
        val jantai: Int,
        val pattern: DecompPattern
    )

    /** 面子组（顺子或刻子） */
    data class MentsuGroup(
        val typeIndices: List<Int>,
        val isSequence: Boolean
    ) {
        val isTriple: Boolean get() = !isSequence
    }

    /** 分解模式 */
    enum class DecompPattern {
        /** 标准形：4面子+1雀头 */
        REGULAR,
        /** 七对子 */
        CHIITOITSU,
        /** 国士无双 */
        KOKUSHI
    }

    /**
     * 将手牌分解为所有可能的标准形组合
     *
     * @param closedTiles 门前手牌（含摸到的牌，共 14 张或 13 张 + 和了牌）
     * @param openMelds 已副露的面子（用于判断手牌中剩余张数）
     * @return 所有可能的分解方式
     */
    fun decompose(closedTiles: List<Tile>): List<Decomposition> {
        val results = mutableListOf<Decomposition>()

        // 统计 34 维计数
        val counts = IntArray(34)
        for (tile in closedTiles) {
            counts[tile.typeIndex]++
        }

        // 1. 尝试标准形分解：4面子 + 1雀头
        decomposeRegular(counts, results)

        // 2. 尝试七对子
        decomposeChiitoitsu(counts, results)

        // 3. 尝试国士无双
        decomposeKokushi(counts, results)

        return results
    }

    /**
     * 标准形递归分解
     */
    private fun decomposeRegular(counts: IntArray, results: MutableList<Decomposition>) {
        val totalTiles = counts.sum()

        // 必须是 14 张（4面子 + 1雀头 = 12 + 2 = 14）
        // 或 2 张（仅雀头，已有 4 副露面子）
        // 等等：实际上是 14 - 3*openMelds 张
        val neededMentsu = (totalTiles - 2) / 3
        if ((totalTiles - 2) % 3 != 0 || neededMentsu < 0 || neededMentsu > 4) return

        // 枚举雀头
        for (jantai in 0 until 34) {
            if (counts[jantai] < 2) continue

            val remaining = counts.copyOf()
            remaining[jantai] -= 2

            val mentsuList = mutableListOf<MentsuGroup>()
            if (extractMentsu(remaining, 0, neededMentsu, mentsuList)) {
                results.add(Decomposition(
                    mentsu = mentsuList.toList(),
                    jantai = jantai,
                    pattern = DecompPattern.REGULAR
                ))
                // 继续搜索其他组合（同一雀头可能有不同面子拆法）
                // 使用深度搜索找所有组合
            }
        }

        // 去重（不同搜索路径可能得到相同结果）
        // 实际上我们需要找到所有不同的拆法，这里用更完整的搜索
        results.clear()
        findAllRegularDecompositions(counts, totalTiles, results)
    }

    /**
     * 查找所有标准形分解（完整搜索）
     */
    private fun findAllRegularDecompositions(
        originalCounts: IntArray,
        totalTiles: Int,
        results: MutableList<Decomposition>
    ) {
        val neededMentsu = (totalTiles - 2) / 3
        if ((totalTiles - 2) % 3 != 0 || neededMentsu < 0 || neededMentsu > 4) return

        for (jantai in 0 until 34) {
            if (originalCounts[jantai] < 2) continue

            val remaining = originalCounts.copyOf()
            remaining[jantai] -= 2

            val allMentsuSets = mutableListOf<List<MentsuGroup>>()
            findAllMentsuCombinations(remaining, 0, neededMentsu, mutableListOf(), allMentsuSets)

            for (mentsuSet in allMentsuSets) {
                results.add(Decomposition(
                    mentsu = mentsuSet,
                    jantai = jantai,
                    pattern = DecompPattern.REGULAR
                ))
            }
        }
    }

    /**
     * 递归查找所有面子组合
     */
    private fun findAllMentsuCombinations(
        counts: IntArray,
        startIndex: Int,
        remaining: Int,
        current: MutableList<MentsuGroup>,
        results: MutableList<List<MentsuGroup>>
    ) {
        if (remaining == 0) {
            // 检查所有计数都为 0
            if (counts.all { it == 0 }) {
                results.add(current.toList())
            }
            return
        }

        // 找到第一个非零的位置
        var idx = startIndex
        while (idx < 34 && counts[idx] == 0) idx++
        if (idx >= 34) return

        // 尝试刻子
        if (counts[idx] >= 3) {
            counts[idx] -= 3
            current.add(MentsuGroup(listOf(idx, idx, idx), isSequence = false))
            findAllMentsuCombinations(counts, idx, remaining - 1, current, results)
            current.removeAt(current.lastIndex)
            counts[idx] += 3
        }

        // 尝试顺子（仅数牌，且同花色内 idx, idx+1, idx+2）
        if (isNumberTileIndex(idx)) {
            val suitBase = (idx / 9) * 9
            val rank = idx - suitBase // 0..8
            if (rank <= 6) { // 最多到 7(=rank 8) 开始的顺子
                val i1 = idx
                val i2 = idx + 1
                val i3 = idx + 2
                if (i2 < suitBase + 9 && i3 < suitBase + 9 &&
                    counts[i1] >= 1 && counts[i2] >= 1 && counts[i3] >= 1) {
                    counts[i1]--; counts[i2]--; counts[i3]--
                    current.add(MentsuGroup(listOf(i1, i2, i3), isSequence = true))
                    findAllMentsuCombinations(counts, idx, remaining - 1, current, results)
                    current.removeAt(current.lastIndex)
                    counts[i1]++; counts[i2]++; counts[i3]++
                }
            }
        }
    }

    /**
     * 简单面子提取（贪心，用于快速判断是否可胡）
     */
    private fun extractMentsu(
        counts: IntArray,
        startIndex: Int,
        remaining: Int,
        result: MutableList<MentsuGroup>
    ): Boolean {
        if (remaining == 0) return counts.all { it == 0 }

        var idx = startIndex
        while (idx < 34 && counts[idx] == 0) idx++
        if (idx >= 34) return false

        // 尝试刻子
        if (counts[idx] >= 3) {
            counts[idx] -= 3
            result.add(MentsuGroup(listOf(idx, idx, idx), isSequence = false))
            if (extractMentsu(counts, idx, remaining - 1, result)) return true
            result.removeAt(result.lastIndex)
            counts[idx] += 3
        }

        // 尝试顺子
        if (isNumberTileIndex(idx)) {
            val suitBase = (idx / 9) * 9
            val rank = idx - suitBase
            if (rank <= 6) {
                val i1 = idx; val i2 = idx + 1; val i3 = idx + 2
                if (i2 < suitBase + 9 && i3 < suitBase + 9 &&
                    counts[i1] >= 1 && counts[i2] >= 1 && counts[i3] >= 1) {
                    counts[i1]--; counts[i2]--; counts[i3]--
                    result.add(MentsuGroup(listOf(i1, i2, i3), isSequence = true))
                    if (extractMentsu(counts, idx, remaining - 1, result)) return true
                    result.removeAt(result.lastIndex)
                    counts[i1]++; counts[i2]++; counts[i3]++
                }
            }
        }

        return false
    }

    /**
     * 七对子分解
     */
    private fun decomposeChiitoitsu(counts: IntArray, results: MutableList<Decomposition>) {
        if (counts.sum() != 14) return

        val pairs = mutableListOf<Int>()
        for (i in 0 until 34) {
            when {
                counts[i] == 2 -> pairs.add(i)
                counts[i] == 4 -> {
                    // 4 张同种牌算 2 组对子
                    pairs.add(i)
                    pairs.add(i)
                }
                counts[i] == 0 -> {} // ok
                else -> return // 1 或 3 张 → 不是七对子
            }
        }

        if (pairs.size == 7) {
            results.add(Decomposition(
                mentsu = emptyList(),
                jantai = -1, // 七对子没有明确的雀头
                pattern = DecompPattern.CHIITOITSU
            ))
        }
    }

    /**
     * 国士无双分解
     */
    private fun decomposeKokushi(counts: IntArray, results: MutableList<Decomposition>) {
        if (counts.sum() != 14) return

        // 国士 13 种幺九牌
        val kokushiIndices = listOf(
            0, 8,       // 1万, 9万
            9, 17,      // 1索, 9索
            18, 26,     // 1筒, 9筒
            27, 28, 29, 30, // 东南西北
            31, 32, 33  // 白发中
        )

        // 检查每种至少 1 张
        for (idx in kokushiIndices) {
            if (counts[idx] < 1) return
        }

        // 确认只使用了幺九牌
        for (i in 0 until 34) {
            if (i !in kokushiIndices && counts[i] > 0) return
        }

        // 找出成对的那种（雀头）
        for (idx in kokushiIndices) {
            if (counts[idx] == 2) {
                results.add(Decomposition(
                    mentsu = emptyList(),
                    jantai = idx,
                    pattern = DecompPattern.KOKUSHI
                ))
            }
        }
    }

    /**
     * 快速判断是否可以和牌（不关心具体拆法，只返回 true/false）
     */
    fun canWin(closedTiles: List<Tile>): Boolean {
        val counts = IntArray(34)
        for (tile in closedTiles) {
            counts[tile.typeIndex]++
        }
        return canWinFromCounts(counts)
    }

    /**
     * 从计数数组判断是否可以和牌
     */
    fun canWinFromCounts(counts: IntArray): Boolean {
        val total = counts.sum()
        if (total != 14 && total != 2 && total != 5 && total != 8 && total != 11) {
            // 非法张数（14, 11, 8, 5, 2 对应 0~4 副露）
            if (total % 3 != 2) return false
        }

        // 标准形
        val neededMentsu = (total - 2) / 3
        if ((total - 2) % 3 == 0 && neededMentsu in 0..4) {
            for (jantai in 0 until 34) {
                if (counts[jantai] < 2) continue
                val remaining = counts.copyOf()
                remaining[jantai] -= 2
                if (canExtractAllMentsu(remaining, 0, neededMentsu)) return true
            }
        }

        // 七对子 (仅当 total == 14)
        if (total == 14) {
            var pairCount = 0
            var valid = true
            for (c in counts) {
                when (c) {
                    0 -> {}
                    2 -> pairCount++
                    4 -> pairCount += 2
                    else -> { valid = false; break }
                }
            }
            if (valid && pairCount == 7) return true
        }

        // 国士无双 (仅当 total == 14)
        if (total == 14 && checkKokushi(counts)) return true

        return false
    }

    /**
     * 计算听牌（待牌列表）
     * @return 和了所需的牌种 typeIndex 列表
     */
    fun calculateWaits(closedTiles: List<Tile>): Set<Int> {
        val counts = IntArray(34)
        for (tile in closedTiles) {
            counts[tile.typeIndex]++
        }

        val waits = mutableSetOf<Int>()
        for (candidate in 0 until 34) {
            if (counts[candidate] >= 4) continue // 已有 4 张，不可能再摸到

            val testCounts = counts.copyOf()
            testCounts[candidate]++
            if (canWinFromCounts(testCounts)) {
                waits.add(candidate)
            }
        }
        return waits
    }

    private fun canExtractAllMentsu(counts: IntArray, startIndex: Int, remaining: Int): Boolean {
        if (remaining == 0) return counts.all { it == 0 }

        var idx = startIndex
        while (idx < 34 && counts[idx] == 0) idx++
        if (idx >= 34) return false

        // 刻子
        if (counts[idx] >= 3) {
            counts[idx] -= 3
            if (canExtractAllMentsu(counts, idx, remaining - 1)) {
                counts[idx] += 3
                return true
            }
            counts[idx] += 3
        }

        // 顺子
        if (isNumberTileIndex(idx)) {
            val suitBase = (idx / 9) * 9
            val rank = idx - suitBase
            if (rank <= 6) {
                val i1 = idx; val i2 = idx + 1; val i3 = idx + 2
                if (i2 < suitBase + 9 && i3 < suitBase + 9 &&
                    counts[i1] >= 1 && counts[i2] >= 1 && counts[i3] >= 1) {
                    counts[i1]--; counts[i2]--; counts[i3]--
                    if (canExtractAllMentsu(counts, idx, remaining - 1)) {
                        counts[i1]++; counts[i2]++; counts[i3]++
                        return true
                    }
                    counts[i1]++; counts[i2]++; counts[i3]++
                }
            }
        }

        return false
    }

    private fun checkKokushi(counts: IntArray): Boolean {
        val kokushiIndices = listOf(0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33)
        var hasPair = false
        for (i in 0 until 34) {
            if (i in kokushiIndices) {
                when {
                    counts[i] == 0 -> return false
                    counts[i] == 1 -> {}
                    counts[i] == 2 && !hasPair -> hasPair = true
                    else -> return false
                }
            } else {
                if (counts[i] > 0) return false
            }
        }
        return hasPair
    }

    /**
     * 判断 typeIndex 是否为数牌（万/索/筒）
     */
    private fun isNumberTileIndex(index: Int): Boolean = index in 0..26

    companion object {
        /** 单例实例 */
        val INSTANCE = HandDecomposer()
    }
}

