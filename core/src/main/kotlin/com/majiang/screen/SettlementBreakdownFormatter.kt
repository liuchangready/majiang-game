package com.majiang.screen

import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.rules.FuLine
import com.majiang.rules.ScoreBreakdown

data class SettlementLayoutLines(
    val winnerText: String,
    val loserText: String?,
    val yakuLines: List<String>,
    val doraLine: String?,
    val fuLines: List<String>,
    val baseLine: String,
    val payLines: List<String>,
    val scoreSummaryHeader: String = "-- SCORE SUMMARY --",
    val scoreSummaryLines: List<String>
)

/**
 * 结算弹窗文本推导行组装纯函数
 *
 * 遵循 DESIGN.md 规范：
 * - 纯函数设计，不依赖 libGDX 图形上下文，支持 headless 契约单测。
 * - 组装役种行、三类宝牌分列、符数等式、基本点公式与封顶、支付分配与本场立直加算、四家分数汇总。
 */
object SettlementBreakdownFormatter {

    fun formatTile(tile: Tile): String = when {
        tile.isRedFive -> when (tile.suit) {
            Suit.MAN -> "0m"
            Suit.PIN -> "0p"
            Suit.SOU -> "0s"
            else -> "0?"
        }
        tile.suit == Suit.MAN -> "${tile.rank}m"
        tile.suit == Suit.PIN -> "${tile.rank}p"
        tile.suit == Suit.SOU -> "${tile.rank}s"
        tile.suit == Suit.WIND -> listOf("東", "南", "西", "北")[tile.rank - 1]
        tile.suit == Suit.DRAGON -> listOf("白", "發", "中")[tile.rank - 1]
        else -> tile.displayName()
    }

    fun formatFuLine(line: FuLine): String {
        return if (line.description.contains("刻") || line.description.contains("杠")) {
            "${line.description}×${line.fu}"
        } else {
            "${line.description}${line.fu}"
        }
    }

    fun format(
        breakdown: ScoreBreakdown,
        winner: Int,
        loser: Int?,
        viewerSeat: Int,
        payments: Map<Int, Int> = emptyMap(),
        newScores: List<Int> = emptyList(),
        currentScores: List<Int> = emptyList()
    ): SettlementLayoutLines {
        val winnerName = if (winner == viewerSeat) "P$winner [YOU]" else "P$winner [AI $winner]"
        val isTsumo = (loser == null)
        val winnerText = if (isTsumo) "WINNER: $winnerName          TSUMO" else "WINNER: $winnerName"
        val loserText = if (loser != null) {
            val lName = if (loser == viewerSeat) "P$loser [YOU]" else "P$loser [AI $loser]"
            "FROM: $lName"
        } else null

        // 1. 役种列表（所有命中的役种逐项列出 + 注明已自动选择最优拆解）
        val yakuParts = if (breakdown.yakumanCount > 0) {
            breakdown.yakuLines.map { "${it.first} (役满)" }
        } else {
            breakdown.yakuLines.map { "${it.first} ${it.second}翻" }
        }
        val yakuJoined = if (yakuParts.isNotEmpty()) yakuParts.joinToString(" | ") else "No Yaku"
        val yakuLines = listOf(
            "Yaku: $yakuJoined",
            "  (已自动选择最优拆解)"
        )

        // 2. 宝牌三类分列（表/里/赤分列 + 命中具体牌列表）
        val doraParts = mutableListOf<String>()
        if (breakdown.dora.indicatorCount > 0) {
            val tiles = breakdown.dora.indicatorTiles.joinToString(",") { formatTile(it) }
            val tilesStr = if (tiles.isNotEmpty()) " $tiles" else ""
            doraParts.add("表 DORA$tilesStr (+${breakdown.dora.indicatorCount})")
        }
        if (breakdown.dora.uraCount > 0) {
            val tiles = breakdown.dora.uraTiles.joinToString(",") { formatTile(it) }
            val tilesStr = if (tiles.isNotEmpty()) " $tiles" else ""
            doraParts.add("里 DORA$tilesStr (+${breakdown.dora.uraCount})")
        }
        if (breakdown.dora.redCount > 0) {
            val tiles = breakdown.dora.redTiles.joinToString(",") { formatTile(it) }
            val tilesStr = if (tiles.isNotEmpty()) " $tiles" else ""
            doraParts.add("赤 RED$tilesStr (+${breakdown.dora.redCount})")
        }
        val doraLine = if (doraParts.isNotEmpty()) "Dora: ${doraParts.joinToString(" | ")}" else null

        // 3. 符数构成表（底符 + 面子符 + 雀头 + 听牌 + 自摸 → 切上）
        val fuLines = mutableListOf<String>()
        if (breakdown.score.isYakuman) {
            fuLines.add("Fu:   役满不计符")
            fuLines.add("      ${breakdown.score.title}")
        } else if (breakdown.fu.specialNote != null && breakdown.fu.lines.size <= 1) {
            fuLines.add("Fu:   ${breakdown.fu.specialNote}")
            fuLines.add("      ${breakdown.score.han}翻${breakdown.fu.finalFu}符")
        } else {
            val eqParts = breakdown.fu.lines.map { formatFuLine(it) }
            val eqStr = eqParts.joinToString(" + ")
            val noteSuffix = if (breakdown.fu.specialNote != null) " (${breakdown.fu.specialNote})" else ""
            fuLines.add("Fu:   $eqStr = ${breakdown.fu.subtotal} → 切上${breakdown.fu.roundedUpTo}$noteSuffix")
            fuLines.add("      ${breakdown.score.han}翻${breakdown.fu.finalFu}符")
        }

        // 4. 基本点公式与封顶原因
        val baseLine = if (breakdown.score.isYakuman) {
            "Base: ${breakdown.score.title} 基本点 ${breakdown.score.basePoints}"
        } else {
            val formula = if (breakdown.score.capReason != null) {
                "${breakdown.score.fu} × 2^(2+${breakdown.score.han}) = ${breakdown.score.rawBasePoints} → ${breakdown.score.capReason} ${breakdown.score.basePoints}"
            } else {
                "${breakdown.score.fu} × 2^(2+${breakdown.score.han}) = ${breakdown.score.rawBasePoints}"
            }
            "Base: $formula"
        }

        // 5. 支付分配与加算
        val payAdditions = mutableListOf<String>()
        if (breakdown.honbaAddition > 0) {
            payAdditions.add("+本场300×${breakdown.honbaAddition / 300}")
        }
        if (breakdown.riichiAddition > 0) {
            payAdditions.add("+立直棒1000×${breakdown.riichiAddition / 1000}")
        }
        val addStr = if (payAdditions.isNotEmpty()) "  (${payAdditions.joinToString(" ")})" else ""
        val payMain = breakdown.paymentLines.joinToString("; ")
        val payLines = listOf(
            "Pay:  $payMain$addStr",
            "      TOTAL ${breakdown.totalPoints}"
        )

        // 6. 四家分数变化
        val scoreSummaryLines = (0 until 4).map { i ->
            val delta = payments[i] ?: 0
            val deltaStr = if (delta >= 0) "+$delta" else "$delta"
            val scoreNow = if (newScores.size == 4) newScores[i] else if (i < currentScores.size) currentScores[i] else 0
            val pName = if (i == viewerSeat) "P$i [YOU]" else "P$i [AI $i]"
            "$pName: $scoreNow pts ($deltaStr)"
        }

        return SettlementLayoutLines(
            winnerText = winnerText,
            loserText = loserText,
            yakuLines = yakuLines,
            doraLine = doraLine,
            fuLines = fuLines,
            baseLine = baseLine,
            payLines = payLines,
            scoreSummaryLines = scoreSummaryLines
        )
    }
}
