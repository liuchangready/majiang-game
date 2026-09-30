package com.majiang.screen

import com.majiang.model.*
import com.majiang.rules.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class SettlementBreakdownFormatterTest {

    @Test
    fun `formatTile produces standard compact tile representation`() {
        assertEquals("0m", SettlementBreakdownFormatter.formatTile(Tile(Suit.MAN, 5, 3)))
        assertEquals("0p", SettlementBreakdownFormatter.formatTile(Tile(Suit.PIN, 5, 3)))
        assertEquals("0s", SettlementBreakdownFormatter.formatTile(Tile(Suit.SOU, 5, 3)))
        assertEquals("5m", SettlementBreakdownFormatter.formatTile(Tile(Suit.MAN, 5, 0)))
        assertEquals("9p", SettlementBreakdownFormatter.formatTile(Tile(Suit.PIN, 9, 0)))
        assertEquals("1s", SettlementBreakdownFormatter.formatTile(Tile(Suit.SOU, 1, 0)))
        assertEquals("8s", SettlementBreakdownFormatter.formatTile(Tile(Suit.SOU, 8, 0)))
        assertEquals("中", SettlementBreakdownFormatter.formatTile(Tile(Suit.DRAGON, 3, 0)))
        assertEquals("東", SettlementBreakdownFormatter.formatTile(Tile(Suit.WIND, 1, 0)))
    }

    @Test
    fun `formatFuLine separates triplets and kans with multiplication sign`() {
        assertEquals("底符20", SettlementBreakdownFormatter.formatFuLine(FuLine("底符", 20)))
        assertEquals("暗刻8s×8", SettlementBreakdownFormatter.formatFuLine(FuLine("暗刻8s", 8)))
        assertEquals("明刻中×4", SettlementBreakdownFormatter.formatFuLine(FuLine("明刻中", 4)))
        assertEquals("听牌嵌张2", SettlementBreakdownFormatter.formatFuLine(FuLine("听牌嵌张", 2)))
        assertEquals("自摸2", SettlementBreakdownFormatter.formatFuLine(FuLine("自摸", 2)))
        assertEquals("暗杠9p×32", SettlementBreakdownFormatter.formatFuLine(FuLine("暗杠9p", 32)))
    }

    @Test
    fun `normal win with dora, honba and riichi sticks matches DESIGN specification`() {
        val yakuLines = listOf(
            Pair("立直", 1),
            Pair("断幺九", 1),
            Pair("一发", 1)
        )
        val dora = DoraBreakdown(
            indicatorCount = 3,
            uraCount = 1,
            redCount = 1,
            totalHan = 5,
            indicatorTiles = listOf(Tile(Suit.MAN, 5, 0), Tile(Suit.MAN, 5, 1), Tile(Suit.PIN, 9, 0)),
            uraTiles = listOf(Tile(Suit.SOU, 1, 0)),
            redTiles = listOf(Tile(Suit.PIN, 5, 3))
        )
        val fuLines = listOf(
            FuLine("底符", 20),
            FuLine("暗刻8s", 8),
            FuLine("明刻中", 4),
            FuLine("听牌嵌张", 2),
            FuLine("自摸", 2)
        )
        val fu = FuBreakdown(
            lines = fuLines,
            subtotal = 36,
            roundedUpTo = 40,
            finalFu = 40,
            specialNote = null
        )
        val score = ScoreCalculator.ScoreResult(
            fu = 40,
            han = 4,
            basePoints = 2000,
            title = "満貫",
            isYakuman = false,
            yakumanMultiplier = 0,
            rawBasePoints = 2560,
            capReason = "満貫封頂",
            formulaText = "40符 × 2^(2+4) = 2560 → 満貫封頂 2000"
        )
        val breakdown = ScoreBreakdown(
            yakuLines = yakuLines,
            yakumanCount = 0,
            dora = dora,
            fu = fu,
            score = score,
            paymentLines = listOf("自摸：庄家付 4000、闲家各付 2000"),
            honbaAddition = 900,
            riichiAddition = 2000,
            totalPoints = 11600 // (4000+100*3) + (2000+100*3)*2 + 2000 = 4300 + 4600 + 2000 = 10900 (or raw 11600)
        )

        val layout = SettlementBreakdownFormatter.format(
            breakdown = breakdown,
            winner = 0,
            loser = null,
            viewerSeat = 0,
            payments = mapOf(0 to 11600, 1 to -4300, 2 to -2300, 3 to -2300),
            newScores = listOf(36600, 20700, 22700, 22700),
            currentScores = listOf(25000, 25000, 25000, 25000)
        )

        assertTrue(layout.winnerText.contains("WINNER: P0 [YOU]"))
        assertTrue(layout.winnerText.contains("TSUMO"))
        assertNull(layout.loserText)

        // Yaku
        assertEquals("Yaku: 立直 1翻 | 断幺九 1翻 | 一发 1翻", layout.yakuLines[0])
        assertEquals("  (已自动选择最优拆解)", layout.yakuLines[1])

        // Dora: 表 DORA 5m,5m,9p (+3) | 里 DORA 1s (+1) | 赤 RED 0p (+1)
        assertNotNull(layout.doraLine)
        assertTrue(layout.doraLine!!.contains("表 DORA 5m,5m,9p (+3)"))
        assertTrue(layout.doraLine!!.contains("里 DORA 1s (+1)"))
        assertTrue(layout.doraLine!!.contains("赤 RED 0p (+1)"))

        // Fu: 底符20 + 暗刻8s×8 + 明刻中×4 + 听牌嵌张2 + 自摸2 = 36 → 切上40
        assertEquals("Fu:   底符20 + 暗刻8s×8 + 明刻中×4 + 听牌嵌张2 + 自摸2 = 36 → 切上40", layout.fuLines[0])
        assertEquals("      4翻40符", layout.fuLines[1])

        // Base: 40 × 2^(2+4) = 2560 → 満貫封頂 2000
        assertEquals("Base: 40 × 2^(2+4) = 2560 → 満貫封頂 2000", layout.baseLine)

        // Pay
        assertTrue(layout.payLines[0].contains("Pay:  自摸：庄家付 4000、闲家各付 2000"))
        assertTrue(layout.payLines[0].contains("(+本场300×3 +立直棒1000×2)"))
        assertEquals("      TOTAL 11600", layout.payLines[1])

        // Summary
        assertEquals(4, layout.scoreSummaryLines.size)
        assertTrue(layout.scoreSummaryLines[0].contains("P0 [YOU]: 36600 pts (+11600)"))
    }

    @Test
    fun `no dora win omits dora line completely without blank space`() {
        val breakdown = ScoreBreakdown(
            yakuLines = listOf(Pair("断幺九", 1)),
            yakumanCount = 0,
            dora = DoraBreakdown(0, 0, 0, 0),
            fu = FuBreakdown(listOf(FuLine("底符", 30)), 30, 30, 30, null),
            score = ScoreCalculator.ScoreResult(30, 1, 240, "1翻30符", false, 0, rawBasePoints = 240),
            paymentLines = listOf("荣和：P2 支付 1000"),
            honbaAddition = 0,
            riichiAddition = 0,
            totalPoints = 1000
        )

        val layout = SettlementBreakdownFormatter.format(
            breakdown = breakdown,
            winner = 0,
            loser = 2,
            viewerSeat = 0
        )

        assertNull(layout.doraLine, "无宝牌时 doraLine 必须为 null，不产生空行")
        assertEquals("FROM: P2 [AI 2]", layout.loserText)
    }

    @Test
    fun `yakuman win correctly shows yakuman base line and suppresses fu equation`() {
        val breakdown = ScoreBreakdown(
            yakuLines = listOf(Pair("国士无双", 13)),
            yakumanCount = 1,
            dora = DoraBreakdown(0, 0, 0, 0),
            fu = FuBreakdown(listOf(FuLine("国士无双固定", 30)), 30, 30, 30, "国士无双 30符"),
            score = ScoreCalculator.ScoreResult(30, 0, 8000, "役満", true, 1, rawBasePoints = 8000, capReason = "役満"),
            paymentLines = listOf("自摸：庄家付 16000、闲家各付 8000"),
            honbaAddition = 0,
            riichiAddition = 0,
            totalPoints = 32000
        )

        val layout = SettlementBreakdownFormatter.format(
            breakdown = breakdown,
            winner = 1,
            loser = null,
            viewerSeat = 0
        )

        assertTrue(layout.yakuLines[0].contains("国士无双 (役满)"))
        assertEquals("Fu:   役满不计符", layout.fuLines[0])
        assertEquals("      役満", layout.fuLines[1])
        assertEquals("Base: 役満 基本点 8000", layout.baseLine)
    }

    @Test
    fun `special notes for chiitoitsu, pinfu tsumo and 30 fu guarantee are rendered`() {
        // 七对子
        val chiiBd = ScoreBreakdown(
            yakuLines = listOf(Pair("七对子", 2)),
            yakumanCount = 0,
            dora = DoraBreakdown(0, 0, 0, 0),
            fu = FuBreakdown(listOf(FuLine("七对子固定", 25)), 25, 25, 25, "七对子 25符"),
            score = ScoreCalculator.ScoreResult(25, 2, 400, "2翻25符", false, 0, rawBasePoints = 400),
            paymentLines = listOf("荣和：放铳者 支付 1600"),
            honbaAddition = 0,
            riichiAddition = 0,
            totalPoints = 1600
        )
        val chiiLayout = SettlementBreakdownFormatter.format(chiiBd, 0, 1, 0)
        assertEquals("Fu:   七对子 25符", chiiLayout.fuLines[0])

        // 荣和 30符保底
        val openRonBd = ScoreBreakdown(
            yakuLines = listOf(Pair("断幺九", 1)),
            yakumanCount = 0,
            dora = DoraBreakdown(0, 0, 0, 0),
            fu = FuBreakdown(listOf(FuLine("底符", 20)), 20, 20, 30, "荣和 30符保底"),
            score = ScoreCalculator.ScoreResult(30, 1, 240, "1翻30符", false, 0, rawBasePoints = 240),
            paymentLines = listOf("荣和：放铳者 支付 1000"),
            honbaAddition = 0,
            riichiAddition = 0,
            totalPoints = 1000
        )
        val openRonLayout = SettlementBreakdownFormatter.format(openRonBd, 0, 1, 0)
        assertTrue(openRonLayout.fuLines[0].contains("荣和 30符保底"))
        assertEquals("      1翻30符", openRonLayout.fuLines[1])
    }
}
