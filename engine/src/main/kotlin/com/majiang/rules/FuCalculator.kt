package com.majiang.rules

import com.majiang.model.*

/**
 * 符数（Fu）计算器
 *
 * 符数由以下要素累加：
 * - 副底：门前荣和 30符, 自摸/非门前 20符 (七对子固定 25符)
 * - 面子符：
 *   - 中张明顺 0, 中张明刻 2, 中张暗刻 4
 *   - 幺九明刻 4, 幺九暗刻 8
 *   - 中张明杠 8, 中张暗杠 16
 *   - 幺九明杠 16, 幺九暗杠 32
 * - 雀头符：
 *   - 役牌（三元牌/自风/场风）雀头 +2
 *   - 连风牌雀头（自风=场风）+2 或 +4（规则争议，本实现取 +2）
 * - 待ち符：
 *   - 两面/双碰 0, 嵌张/边张/单骑 2
 *
 * 最终向上取整到 10 的倍数
 */
data class FuLine(
    val description: String,
    val fu: Int
)

data class FuBreakdown(
    val lines: List<FuLine>,
    val subtotal: Int,
    val roundedUpTo: Int,
    val finalFu: Int,
    val specialNote: String? = null
)

class FuCalculator {

    /**
     * 计算符数明细推导
     */
    fun calculateBreakdown(
        decomposition: HandDecomposer.Decomposition,
        winTile: Tile,
        isTsumo: Boolean,
        isClosed: Boolean,
        seatWind: Int,
        roundWind: Int,
        openMelds: List<Meld> = emptyList()
    ): FuBreakdown {
        // 七对子固定 25 符
        if (decomposition.pattern == HandDecomposer.DecompPattern.CHIITOITSU) {
            val lines = listOf(FuLine("七对子固定", 25))
            return FuBreakdown(
                lines = lines,
                subtotal = 25,
                roundedUpTo = 25,
                finalFu = 25,
                specialNote = "七对子 25符"
            )
        }

        // 国士无双不计符（役满直接算点）
        if (decomposition.pattern == HandDecomposer.DecompPattern.KOKUSHI) {
            val lines = listOf(FuLine("国士无双固定", 30))
            return FuBreakdown(
                lines = lines,
                subtotal = 30,
                roundedUpTo = 30,
                finalFu = 30,
                specialNote = "国士无双 30符"
            )
        }

        val winTypeIndex = winTile.typeIndex

        // 平和自摸特殊：固定 20 符（门前平和自摸舍弃2符自摸以成立平和）
        if (isTsumo && isClosed && isPinfu(decomposition, winTypeIndex, seatWind, roundWind)) {
            val lines = listOf(FuLine("底符", 20))
            return FuBreakdown(
                lines = lines,
                subtotal = 20,
                roundedUpTo = 20,
                finalFu = 20,
                specialNote = "平和自摸 20符"
            )
        }

        val lines = mutableListOf<FuLine>()

        // 1. 副底
        val baseFu = if (isTsumo) {
            20 // 自摸副底
        } else if (isClosed) {
            30 // 门前荣和副底
        } else {
            20 // 非门前荣和（喰い平和）副底
        }
        lines.add(FuLine("底符", baseFu))

        // 2. 面子符 — 门前刻子（若荣和完成刻子，该刻子按明刻计算）
        for (mentsu in decomposition.mentsu) {
            val isOpen = !isTsumo && !mentsu.isSequence &&
                    mentsu.typeIndices[0] == winTypeIndex &&
                    decomposition.jantai != winTypeIndex
            val fu = calculateMentsuFu(mentsu, isOpen)
            if (fu > 0) {
                val tName = tileCompactName(mentsu.typeIndices[0])
                val desc = if (isOpen) "明刻$tName" else "暗刻$tName"
                lines.add(FuLine(desc, fu))
            }
        }

        // 3. 面子符 — 副露面子
        for (meld in openMelds) {
            val fu = calculateMeldFu(meld)
            if (fu > 0) {
                val tName = tileCompactName(meld.tiles.first().typeIndex)
                val desc = when (meld.type) {
                    MeldType.CHI -> "吃$tName"
                    MeldType.PON -> "明刻$tName"
                    MeldType.OPEN_KAN -> "明杠$tName"
                    MeldType.CLOSED_KAN -> "暗杠$tName"
                    MeldType.ADDED_KAN -> "加杠$tName"
                }
                lines.add(FuLine(desc, fu))
            }
        }

        // 4. 雀头符
        if (decomposition.jantai >= 0) {
            val fu = calculateJantaiFu(decomposition.jantai, seatWind, roundWind)
            if (fu > 0) {
                val tName = tileCompactName(decomposition.jantai)
                lines.add(FuLine("雀头$tName", fu))
            }
        }

        // 5. 待ち符
        val waitDesc = determineWaitDescription(decomposition, winTypeIndex)
        if (waitDesc != null) {
            lines.add(FuLine(waitDesc, 2))
        }

        // 6. 自摸符（门前自摸加 2 符）
        if (isTsumo) {
            lines.add(FuLine("自摸", 2))
        }

        // 取整到 10 的倍数（向上）
        val subtotal = lines.sumOf { it.fu }
        val rounded = roundUpTo10(subtotal)
        // 荣和最低 30 符（喰い平和等副露荣和最低 30 符；七对子在开头已单独返回 25 符）
        val (finalFu, specialNote) = if (!isTsumo && rounded < 30) {
            Pair(30, "荣和 30符保底")
        } else {
            Pair(rounded, null)
        }

        return FuBreakdown(
            lines = lines,
            subtotal = subtotal,
            roundedUpTo = rounded,
            finalFu = finalFu,
            specialNote = specialNote
        )
    }

    /**
     * 计算符数（已取整到 10 的倍数，委托 calculateBreakdown 实现）
     */
    fun calculate(
        decomposition: HandDecomposer.Decomposition,
        winTile: Tile,
        isTsumo: Boolean,
        isClosed: Boolean,
        seatWind: Int,
        roundWind: Int,
        openMelds: List<Meld> = emptyList()
    ): Int {
        return calculateBreakdown(
            decomposition = decomposition,
            winTile = winTile,
            isTsumo = isTsumo,
            isClosed = isClosed,
            seatWind = seatWind,
            roundWind = roundWind,
            openMelds = openMelds
        ).finalFu
    }

    /**
     * 计算门前暗面子的符
     */
    private fun calculateMentsuFu(mentsu: HandDecomposer.MentsuGroup, isOpen: Boolean): Int {
        if (mentsu.isSequence) return 0 // 顺子 0 符

        // 刻子
        val typeIndex = mentsu.typeIndices[0]
        val isTerminalOrHonor = isTerminalOrHonorIndex(typeIndex)
        val baseFu = if (isTerminalOrHonor) 4 else 2
        return if (isOpen) baseFu else baseFu * 2 // 暗刻 ×2
    }

    /**
     * 计算副露面子的符
     */
    private fun calculateMeldFu(meld: Meld): Int {
        return when (meld.type) {
            MeldType.CHI -> 0 // 吃 = 顺子，0 符
            MeldType.PON -> {
                val isYaochu = meld.tiles.first().isTerminalOrHonor
                if (isYaochu) 4 else 2 // 明刻
            }
            MeldType.OPEN_KAN -> {
                val isYaochu = meld.tiles.first().isTerminalOrHonor
                if (isYaochu) 16 else 8 // 明杠
            }
            MeldType.CLOSED_KAN -> {
                val isYaochu = meld.tiles.first().isTerminalOrHonor
                if (isYaochu) 32 else 16 // 暗杠
            }
            MeldType.ADDED_KAN -> {
                val isYaochu = meld.tiles.first().isTerminalOrHonor
                if (isYaochu) 16 else 8 // 加杠 = 明杠
            }
        }
    }

    /**
     * 计算雀头符
     */
    private fun calculateJantaiFu(jantaiIndex: Int, seatWind: Int, roundWind: Int): Int {
        // 三元牌雀头
        if (jantaiIndex in 31..33) return 2

        // 风牌雀头
        if (jantaiIndex in 27..30) {
            val windRank = jantaiIndex - 27 + 1 // 1=东, 2=南, 3=西, 4=北
            var fu = 0
            if (windRank == seatWind) fu += 2   // 自风
            if (windRank == roundWind) fu += 2   // 场风
            return fu
        }

        return 0
    }

    /**
     * 判断待ち（听牌形状）说明
     * - 单骑待ち / 嵌张待ち / 边张待ち
     */
    private fun determineWaitDescription(
        decomposition: HandDecomposer.Decomposition,
        winTypeIndex: Int
    ): String? {
        if (decomposition.jantai == winTypeIndex) return "听牌单骑"

        for (mentsu in decomposition.mentsu) {
            if (winTypeIndex !in mentsu.typeIndices) continue

            if (mentsu.isSequence) {
                val sorted = mentsu.typeIndices.sorted()
                val suitBase = (sorted[0] / 9) * 9
                val ranks = sorted.map { it - suitBase } // 0..8 相对值

                // 嵌张：和了牌在中间
                if (ranks[1] == winTypeIndex - suitBase && ranks[0] + 2 == ranks[2]) return "听牌嵌张"

                // 边张：12 待 3，或 89 待 7
                val winRank = winTypeIndex - suitBase
                if (ranks[0] == 0 && ranks[2] == 2 && winRank == 2) return "听牌边张"
                if (ranks[0] == 6 && ranks[2] == 8 && winRank == 6) return "听牌边张"
            }
        }
        return null
    }

    /**
     * 计算待ち（听牌形状）的符
     * - 两面待ち / 双碰待ち → 0符
     * - 嵌张待ち / 边张待ち / 单骑待ち → 2符
     */
    private fun calculateWaitFu(
        decomposition: HandDecomposer.Decomposition,
        winTypeIndex: Int
    ): Int {
        return if (determineWaitDescription(decomposition, winTypeIndex) != null) 2 else 0
    }

    private fun tileCompactName(typeIndex: Int): String = when {
        typeIndex in 0..8 -> "${typeIndex + 1}m"
        typeIndex in 9..17 -> "${typeIndex - 9 + 1}s"
        typeIndex in 18..26 -> "${typeIndex - 18 + 1}p"
        typeIndex in 27..30 -> listOf("東", "南", "西", "北")[typeIndex - 27]
        typeIndex in 31..33 -> listOf("白", "發", "中")[typeIndex - 31]
        else -> "?"
    }

    /**
     * 判断是否为平和
     */
    private fun isPinfu(
        decomposition: HandDecomposer.Decomposition,
        winTypeIndex: Int,
        seatWind: Int,
        roundWind: Int
    ): Boolean {
        if (decomposition.pattern != HandDecomposer.DecompPattern.REGULAR) return false

        // 全部面子必须是顺子
        if (decomposition.mentsu.any { !it.isSequence }) return false

        // 雀头不能是役牌
        if (calculateJantaiFu(decomposition.jantai, seatWind, roundWind) > 0) return false

        // 必须是两面待ち
        for (mentsu in decomposition.mentsu) {
            if (winTypeIndex !in mentsu.typeIndices) continue
            if (mentsu.isSequence) {
                val sorted = mentsu.typeIndices.sorted()
                val suitBase = (sorted[0] / 9) * 9
                val winRank = winTypeIndex - suitBase
                val minRank = sorted[0] - suitBase
                val maxRank = sorted[2] - suitBase

                // 两面：和了牌在端点，且非边张
                if (winRank == minRank && maxRank < 8) return true  // 和了牌在左端
                if (winRank == maxRank && minRank > 0) return true  // 和了牌在右端
            }
        }

        return false
    }

    private fun isTerminalOrHonorIndex(typeIndex: Int): Boolean {
        if (typeIndex >= 27) return true // 字牌
        val rank = typeIndex % 9
        return rank == 0 || rank == 8 // 1 或 9
    }

    private fun roundUpTo10(value: Int): Int = ((value + 9) / 10) * 10

    companion object {
        val INSTANCE = FuCalculator()
    }
}

