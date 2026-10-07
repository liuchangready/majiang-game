package com.majiang.presentation.common

import com.majiang.client.api.DoraBreakdownView
import com.majiang.client.api.DoraIndicatorHitView
import com.majiang.client.api.FuBreakdownView
import com.majiang.client.api.FuLineView
import com.majiang.client.api.ScoreBreakdownView
import com.majiang.client.api.ScoreResultView
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class SettlementDetailPresenterTest {

    private fun tile(suit: SuitView, rank: Int, copyIndex: Int = 0): TileView {
        val base = when (suit) {
            SuitView.MAN -> (rank - 1) * 4
            SuitView.SOU -> 36 + (rank - 1) * 4
            SuitView.PIN -> 72 + (rank - 1) * 4
            SuitView.WIND -> 108 + (rank - 1) * 4
            SuitView.DRAGON -> 124 + (rank - 1) * 4
        }
        return TileView(base + copyIndex, suit, rank)
    }

    private fun fixture() = ScoreBreakdownView(
        yakuLines = listOf(
            "立直" to 1,
            "一发" to 1,
            "门前清自摸和" to 1,
            "平和" to 1,
            "断幺九" to 1,
            "一杯口" to 1,
            "清一色" to 6
        ),
        yakumanCount = 0,
        dora = DoraBreakdownView(
            indicatorCount = 10,
            uraCount = 10,
            redCount = 3,
            totalHan = 23,
            indicatorTiles = List(10) { tile(SuitView.MAN, 5, it % 2) },
            uraTiles = List(10) { tile(SuitView.MAN, 5, it % 2) },
            redTiles = listOf(tile(SuitView.MAN, 5, 3), tile(SuitView.PIN, 5, 3), tile(SuitView.SOU, 5, 3)),
            indicatorHits = List(5) {
                DoraIndicatorHitView(
                    indicator = tile(SuitView.MAN, 4, it % 4),
                    target = tile(SuitView.MAN, 5),
                    tiles = listOf(tile(SuitView.MAN, 5), tile(SuitView.MAN, 5, 1))
                )
            },
            uraHits = List(5) {
                DoraIndicatorHitView(
                    indicator = tile(SuitView.PIN, 4, it % 4),
                    target = tile(SuitView.PIN, 5),
                    tiles = listOf(tile(SuitView.PIN, 5), tile(SuitView.PIN, 5, 1))
                )
            }
        ),
        fu = FuBreakdownView(
            lines = listOf(FuLineView("平和自摸", 20)),
            subtotal = 20,
            roundedUpTo = 20,
            finalFu = 20,
            specialNote = "平和自摸20符"
        ),
        score = ScoreResultView(
            fu = 20,
            han = 35,
            basePoints = 8000,
            title = "累计役满",
            isYakuman = true,
            yakumanMultiplier = 1,
            rawBasePoints = 8000,
            formulaText = "累计役满封顶"
        ),
        paymentLines = listOf("自摸：庄家付16000，闲家各付8000"),
        honbaAddition = 600,
        riichiAddition = 2000,
        totalPoints = 34600,
        uraEligible = true,
        isDealer = false,
        isTsumo = true
    )

    @Test
    fun `every yaku and every indicator remains reachable exactly once through pages`() {
        val b = fixture()
        for (section in SettlementSection.entries) {
            val rows = SettlementDetailPresenter.rows(b, section)
            val pages = (0 until SettlementDetailPresenter.pageCount(b, section)).map {
                SettlementDetailPresenter.page(b, section, it)
            }
            assertEquals(rows, pages.flatten())
            assertTrue(pages.all { it.size <= 6 && it.isNotEmpty() })
        }
        val yakuRows = SettlementDetailPresenter.rows(b, SettlementSection.YAKU)
        for ((name, _) in b.yakuLines) {
            assertEquals(1, yakuRows.count { it.text.contains(name) }, name)
        }
        val doraRows = SettlementDetailPresenter.rows(b, SettlementSection.DORA)
        for (hit in b.dora.indicatorHits + b.dora.uraHits) {
            assertTrue(
                doraRows.any { it.tiles == listOf(hit.indicator, hit.target) + hit.tiles },
                "Mapping must include actual indicator, target and hits"
            )
        }
        assertTrue(SettlementDetailPresenter.pageCount(b, SettlementSection.DORA) >= 2)
    }

    @Test
    fun `zero categories and ineligible ura remain explicit without leaking tiles`() {
        val b = fixture().copy(
            dora = DoraBreakdownView(
                indicatorCount = 0,
                uraCount = 0,
                redCount = 0,
                totalHan = 0,
                indicatorTiles = emptyList(),
                uraTiles = emptyList(),
                redTiles = emptyList(),
                indicatorHits = emptyList(),
                uraHits = emptyList()
            ),
            uraEligible = false
        )
        val rows = SettlementDetailPresenter.rows(b, SettlementSection.DORA)
        val text = rows.joinToString("\n") { it.text }
        assertTrue(text.contains("表") || text.contains("宝牌"))
        assertTrue(text.contains("赤"))
        assertTrue(text.contains("未立直") || text.contains("不适用"))
        assertTrue(text.contains("0"))
        assertTrue(rows.all { it.tiles.isEmpty() })
    }

    @Test
    fun `yakuman explicitly excludes bonus han and ordinary fu formula`() {
        val b = fixture().copy(
            yakuLines = listOf("大三元" to 0, "字一色" to 0),
            yakumanCount = 2,
            score = ScoreResultView(
                fu = 30,
                han = 0,
                basePoints = 16000,
                title = "两倍役满",
                isYakuman = true,
                yakumanMultiplier = 2,
                rawBasePoints = 16000,
                formulaText = "2倍役满"
            ),
            totalPoints = 66600
        )
        val dora = SettlementDetailPresenter.rows(b, SettlementSection.DORA).joinToString("\n") { it.text }
        val calc = SettlementDetailPresenter.rows(b, SettlementSection.CALC).joinToString("\n") { it.text }
        assertTrue(dora.contains("不计"), dora)
        assertTrue(calc.contains("8000") && calc.contains("2"), calc)
        assertFalse(calc.contains("2^("), calc)
    }

    @Test
    fun `each fu contribution is individually readable and multiple honba are multiplied per payer`() {
        val b = fixture().copy(
            yakuLines = listOf("两立直" to 2, "门前清自摸和" to 1, "役牌 白" to 1),
            dora = DoraBreakdownView(
                indicatorCount = 0,
                uraCount = 0,
                redCount = 0,
                totalHan = 0,
                indicatorTiles = emptyList(),
                uraTiles = emptyList(),
                redTiles = emptyList(),
                indicatorHits = emptyList(),
                uraHits = emptyList()
            ),
            fu = FuBreakdownView(
                lines = listOf(
                    FuLineView("副底", 20),
                    FuLineView("幺九暗刻", 8),
                    FuLineView("中张暗刻", 4),
                    FuLineView("役牌雀头", 2),
                    FuLineView("嵌张", 2),
                    FuLineView("自摸", 2)
                ),
                subtotal = 38,
                roundedUpTo = 40,
                finalFu = 40
            ),
            score = ScoreResultView(
                fu = 40,
                han = 4,
                basePoints = 2000,
                title = "满贯",
                isYakuman = false,
                yakumanMultiplier = 0,
                rawBasePoints = 2560,
                capReason = "满贯封顶",
                formulaText = "40符 4番"
            ),
            honbaAddition = 600, // 2 本场：闲家自摸时每人付 100/本场 -> 200/人
            totalPoints = 10600
        )
        val rows = SettlementDetailPresenter.rows(b, SettlementSection.CALC)
        for (line in b.fu.lines) {
            assertTrue(
                rows.any { it.text.contains(line.description) && it.text.contains(line.fu.toString()) && it.text.length < 55 },
                line.description
            )
        }
        assertEquals(
            b.fu.lines.size,
            b.fu.lines.map { line -> rows.indexOfFirst { it.text.contains(line.description) } }.toSet().size,
            "Fu items must occupy individual rows"
        )
        val text = rows.joinToString("\n") { it.text }
        assertTrue(rows.any { it.text.contains("本场") && Regex("(?<![0-9])200(?![0-9])").containsMatchIn(it.text) }, "Two honba must show 200 per payer: $text")
        assertTrue(text.contains("2560") && text.contains("2000"), text)
    }

    @Test
    fun `calculation shows unrounded and rounded payments for all four winner roles`() {
        for (dealer in listOf(false, true)) {
            for (tsumo in listOf(false, true)) {
                val fu = if (tsumo) 20 else 30
                val han = if (tsumo) 2 else 1
                val basePts = fu * (1 shl (han + 2)) // 20 * 16 = 320 or 30 * 8 = 240
                val score = ScoreResultView(
                    fu = fu,
                    han = han,
                    basePoints = basePts,
                    title = "",
                    isYakuman = false,
                    yakumanMultiplier = 0,
                    rawBasePoints = basePts,
                    formulaText = "$fu 符 $han 番"
                )
                val b = fixture().copy(
                    yakuLines = if (tsumo) listOf("门前清自摸和" to 1, "平和" to 1) else listOf("平和" to 1),
                    dora = DoraBreakdownView(0, 0, 0, 0, emptyList(), emptyList(), emptyList(), emptyList(), emptyList()),
                    fu = FuBreakdownView(emptyList(), fu, fu, fu),
                    score = score,
                    isDealer = dealer,
                    isTsumo = tsumo,
                    honbaAddition = 0,
                    riichiAddition = 0,
                    totalPoints = 1000,
                    paymentLines = emptyList()
                )
                val text = SettlementDetailPresenter.rows(b, SettlementSection.CALC).joinToString("\n") { it.text }
                // basePts = 320 for tsumo 20符2番 (unrounded: 640/320, rounded: 700/400; if dealer: unrounded: 640, rounded: 700)
                // basePts = 240 for ron 30符1番 (unrounded: 1440/960, rounded: 1500/1000)
                val expected = when {
                    tsumo && dealer -> listOf(640, 700)
                    tsumo -> listOf(640, 700, 320, 400)
                    dealer -> listOf(1440, 1500)
                    else -> listOf(960, 1000)
                }
                for (number in expected) {
                    assertTrue(
                        Regex("(?<![0-9])$number(?![0-9])").containsMatchIn(text),
                        "dealer=$dealer tsumo=$tsumo missing $number: $text"
                    )
                }
            }
        }
    }

    @Test
    fun `open ron minimum is explained separately from rounding twenty fu`() {
        val b = fixture().copy(
            yakuLines = listOf("断幺九" to 1),
            dora = DoraBreakdownView(0, 0, 0, 0, emptyList(), emptyList(), emptyList(), emptyList(), emptyList()),
            fu = FuBreakdownView(listOf(FuLineView("副底", 20)), 20, 20, 30, "荣和 30符保底"),
            score = ScoreResultView(30, 1, 240, "", false, 0, 240, null, "30符 1番"),
            isTsumo = false,
            totalPoints = 1000,
            honbaAddition = 0,
            riichiAddition = 0,
            paymentLines = emptyList()
        )
        val rows = SettlementDetailPresenter.rows(b, SettlementSection.CALC)
        assertTrue(rows.any { it.text.contains("保底") && it.text.contains("30") })
        assertFalse(rows.any { it.text.contains("取整至十位") && it.text.endsWith("30 符") }, "20 rounded up to ten is 20, the 30 fu minimum is a separate rule")
    }
}
