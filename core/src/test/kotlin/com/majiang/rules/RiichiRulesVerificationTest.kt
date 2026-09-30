package com.majiang.rules

import com.majiang.model.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * 日麻规则验证测试
 *
 * 对照 riichi_rules_design.md 中的得点对照表验证计分正确性
 */
class RiichiRulesVerificationTest {

    private val scoreCalc = ScoreCalculator.INSTANCE
    private val fuCalc = FuCalculator.INSTANCE
    private val decomposer = HandDecomposer.INSTANCE
    private val ruleEngine = RiichiRuleEngine.INSTANCE

    // ============================================================
    // 第十章：得点计算验证
    // ============================================================

    @Test
    fun `闲家荣和得点表 - 1翻`() {
        // 1翻30符 → 闲家荣和 1000
        assertNonDealerRon(30, 1, 1000)
        // 1翻40符 → 1300
        assertNonDealerRon(40, 1, 1300)
        // 1翻50符 → 1600
        assertNonDealerRon(50, 1, 1600)
        // 1翻60符 → 2000
        assertNonDealerRon(60, 1, 2000)
        // 1翻70符 → 2300
        assertNonDealerRon(70, 1, 2300)
    }

    @Test
    fun `闲家荣和得点表 - 2翻`() {
        assertNonDealerRon(25, 2, 1600)
        assertNonDealerRon(30, 2, 2000)
        assertNonDealerRon(40, 2, 2600)
        assertNonDealerRon(50, 2, 3200)
        assertNonDealerRon(60, 2, 3900)
        assertNonDealerRon(70, 2, 4500)
    }

    @Test
    fun `闲家荣和得点表 - 3翻`() {
        assertNonDealerRon(25, 3, 3200)
        assertNonDealerRon(30, 3, 3900)
        assertNonDealerRon(40, 3, 5200)
        assertNonDealerRon(50, 3, 6400)
        assertNonDealerRon(60, 3, 7700)
        // 3翻70符 → 满贯 8000
        assertNonDealerRon(70, 3, 8000)
    }

    @Test
    fun `闲家荣和得点表 - 4翻`() {
        assertNonDealerRon(25, 4, 6400)
        assertNonDealerRon(30, 4, 7700)
        // 4翻40符以上 → 满贯 8000
        assertNonDealerRon(40, 4, 8000)
        assertNonDealerRon(50, 4, 8000)
    }

    @Test
    fun `庄家荣和得点表 - 1翻`() {
        assertDealerRon(30, 1, 1500)
        assertDealerRon(40, 1, 2000)
        assertDealerRon(50, 1, 2400)
        assertDealerRon(60, 1, 2900)
    }

    @Test
    fun `庄家荣和得点表 - 2翻`() {
        assertDealerRon(25, 2, 2400)
        assertDealerRon(30, 2, 2900)
        assertDealerRon(40, 2, 3900)
        assertDealerRon(50, 2, 4800)
        assertDealerRon(60, 2, 5800)
    }

    @Test
    fun `庄家荣和得点表 - 3翻`() {
        assertDealerRon(25, 3, 4800)
        assertDealerRon(30, 3, 5800)
        assertDealerRon(40, 3, 7700)
        assertDealerRon(50, 3, 9600)
        assertDealerRon(60, 3, 11600)
        // 3翻70符 → 满贯 12000
        assertDealerRon(70, 3, 12000)
    }

    @Test
    fun `庄家荣和得点表 - 4翻`() {
        assertDealerRon(25, 4, 9600)
        assertDealerRon(30, 4, 11600)
        // 4翻40符以上 → 满贯 12000
        assertDealerRon(40, 4, 12000)
    }

    // ============================================================
    // 满贯及以上固定得点
    // ============================================================

    @Test
    fun `满贯 - 5翻`() {
        val result = scoreCalc.calculate(30, 5)
        assertEquals(2000, result.basePoints, "满贯基本点")
        assertEquals(8000, result.nonDealerRonPayment())
        assertEquals(12000, result.dealerRonPayment())
    }

    @Test
    fun `跳满 - 6翻`() {
        val result = scoreCalc.calculate(30, 6)
        assertEquals(3000, result.basePoints)
        assertEquals(12000, result.nonDealerRonPayment())
        assertEquals(18000, result.dealerRonPayment())
    }

    @Test
    fun `跳满 - 7翻`() {
        val result = scoreCalc.calculate(30, 7)
        assertEquals(3000, result.basePoints)
    }

    @Test
    fun `倍满 - 8翻`() {
        val result = scoreCalc.calculate(30, 8)
        assertEquals(4000, result.basePoints)
        assertEquals(16000, result.nonDealerRonPayment())
        assertEquals(24000, result.dealerRonPayment())
    }

    @Test
    fun `倍满 - 10翻`() {
        val result = scoreCalc.calculate(30, 10)
        assertEquals(4000, result.basePoints)
    }

    @Test
    fun `三倍满 - 11翻`() {
        val result = scoreCalc.calculate(30, 11)
        assertEquals(6000, result.basePoints)
        assertEquals(24000, result.nonDealerRonPayment())
        assertEquals(36000, result.dealerRonPayment())
    }

    @Test
    fun `累计役满 - 13翻`() {
        val result = scoreCalc.calculate(30, 13)
        assertEquals(8000, result.basePoints)
        assertEquals(32000, result.nonDealerRonPayment())
        assertEquals(48000, result.dealerRonPayment())
    }

    @Test
    fun `役满`() {
        val result = scoreCalc.calculate(0, 0, yakumanCount = 1)
        assertTrue(result.isYakuman)
        assertEquals(8000, result.basePoints)
        assertEquals(32000, result.nonDealerRonPayment())
        assertEquals(48000, result.dealerRonPayment())
    }

    @Test
    fun `双倍役满`() {
        val result = scoreCalc.calculate(0, 0, yakumanCount = 2)
        assertTrue(result.isYakuman)
        assertEquals(16000, result.basePoints)
        assertEquals(64000, result.nonDealerRonPayment())
        assertEquals(96000, result.dealerRonPayment())
    }

    // ============================================================
    // 自摸支付验证
    // ============================================================

    @Test
    fun `闲家自摸 - 满贯`() {
        val result = scoreCalc.calculate(30, 5)
        val (dealerPay, otherPay) = result.nonDealerTsumoPayment()
        assertEquals(4000, dealerPay)
        assertEquals(2000, otherPay)
    }

    @Test
    fun `庄家自摸 - 满贯`() {
        val result = scoreCalc.calculate(30, 5)
        assertEquals(4000, result.dealerTsumoPayment())
    }

    @Test
    fun `闲家自摸 - 役满`() {
        val result = scoreCalc.calculate(0, 0, yakumanCount = 1)
        val (dealerPay, otherPay) = result.nonDealerTsumoPayment()
        assertEquals(16000, dealerPay)
        assertEquals(8000, otherPay)
    }

    @Test
    fun `庄家自摸 - 役满`() {
        val result = scoreCalc.calculate(0, 0, yakumanCount = 1)
        assertEquals(16000, result.dealerTsumoPayment())
    }

    // ============================================================
    // 向上取整验证
    // ============================================================

    @Test
    fun `向上取整到100`() {
        assertEquals(100, ScoreCalculator.roundUpTo100(1))
        assertEquals(100, ScoreCalculator.roundUpTo100(100))
        assertEquals(200, ScoreCalculator.roundUpTo100(101))
        assertEquals(1000, ScoreCalculator.roundUpTo100(1000))
        assertEquals(1100, ScoreCalculator.roundUpTo100(1001))
    }

    // ============================================================
    // 第四章：和牌形判定
    // ============================================================

    @Test
    fun `标准形和牌判定 - 4面子+1雀头`() {
        // 123万 456万 789索 444筒 55筒
        val tiles = listOf(
            Tile(Suit.MAN, 1), Tile(Suit.MAN, 2), Tile(Suit.MAN, 3),
            Tile(Suit.MAN, 4), Tile(Suit.MAN, 5), Tile(Suit.MAN, 6),
            Tile(Suit.SOU, 7), Tile(Suit.SOU, 8), Tile(Suit.SOU, 9),
            Tile(Suit.PIN, 4), Tile(Suit.PIN, 4, 1), Tile(Suit.PIN, 4, 2),
            Tile(Suit.PIN, 5), Tile(Suit.PIN, 5, 1)
        )
        assertTrue(decomposer.canWin(tiles), "4面子+1雀头应当和了")
    }

    @Test
    fun `七对子和牌判定`() {
        val tiles = listOf(
            Tile(Suit.MAN, 1), Tile(Suit.MAN, 1, 1),
            Tile(Suit.MAN, 3), Tile(Suit.MAN, 3, 1),
            Tile(Suit.SOU, 5), Tile(Suit.SOU, 5, 1),
            Tile(Suit.PIN, 7), Tile(Suit.PIN, 7, 1),
            Tile(Suit.WIND, 1), Tile(Suit.WIND, 1, 1),
            Tile(Suit.WIND, 3), Tile(Suit.WIND, 3, 1),
            Tile(Suit.DRAGON, 1), Tile(Suit.DRAGON, 1, 1)
        )
        assertTrue(decomposer.canWin(tiles), "七对子应当和了")
    }

    @Test
    fun `国士无双和牌判定`() {
        val tiles = listOf(
            Tile(Suit.MAN, 1), Tile(Suit.MAN, 9),
            Tile(Suit.SOU, 1), Tile(Suit.SOU, 9),
            Tile(Suit.PIN, 1), Tile(Suit.PIN, 9),
            Tile(Suit.WIND, 1), Tile(Suit.WIND, 2), Tile(Suit.WIND, 3), Tile(Suit.WIND, 4),
            Tile(Suit.DRAGON, 1), Tile(Suit.DRAGON, 2), Tile(Suit.DRAGON, 3),
            Tile(Suit.MAN, 1, 1) // 1万成对
        )
        assertTrue(decomposer.canWin(tiles), "国士无双应当和了")
    }

    @Test
    fun `不完整手牌不应和了`() {
        val tiles = listOf(
            Tile(Suit.MAN, 1), Tile(Suit.MAN, 2), Tile(Suit.MAN, 3),
            Tile(Suit.MAN, 4), Tile(Suit.MAN, 5), Tile(Suit.MAN, 6),
            Tile(Suit.SOU, 7), Tile(Suit.SOU, 8), Tile(Suit.SOU, 9),
            Tile(Suit.PIN, 4), Tile(Suit.PIN, 4, 1), Tile(Suit.PIN, 4, 2),
            Tile(Suit.PIN, 5), Tile(Suit.DRAGON, 1) // 5筒+白 不成对
        )
        assertFalse(decomposer.canWin(tiles), "不完整手牌不应和了")
    }

    // ============================================================
    // 听牌计算
    // ============================================================

    @Test
    fun `两面听计算`() {
        // 123万 456万 789索 44筒 → 听 3筒 或 6筒
        val tiles = listOf(
            Tile(Suit.MAN, 1), Tile(Suit.MAN, 2), Tile(Suit.MAN, 3),
            Tile(Suit.MAN, 4), Tile(Suit.MAN, 5), Tile(Suit.MAN, 6),
            Tile(Suit.SOU, 7), Tile(Suit.SOU, 8), Tile(Suit.SOU, 9),
            Tile(Suit.PIN, 4), Tile(Suit.PIN, 5),
            Tile(Suit.PIN, 7), Tile(Suit.PIN, 7, 1)
        )
        val waits = decomposer.calculateWaits(tiles)
        assertTrue(waits.isNotEmpty(), "应该听到牌")
    }

    // ============================================================
    // 第五章：副露判定
    // ============================================================

    @Test
    fun `吃的合法性 - 仅上家`() {
        val hand = PlayerHand()
        hand.closedTiles.addAll(listOf(
            Tile(Suit.MAN, 4), Tile(Suit.MAN, 6),
            Tile(Suit.SOU, 1), Tile(Suit.SOU, 2), Tile(Suit.SOU, 3),
            Tile(Suit.PIN, 1), Tile(Suit.PIN, 2), Tile(Suit.PIN, 3),
            Tile(Suit.WIND, 1), Tile(Suit.WIND, 1, 1), Tile(Suit.WIND, 1, 2),
            Tile(Suit.DRAGON, 1), Tile(Suit.DRAGON, 1, 1)
        ))
        val discardTile = Tile(Suit.MAN, 5) // 5万

        // 上家(player 0)打出，当前玩家(player 1)可以吃
        val options = ruleEngine.canChi(hand, discardTile, fromPlayer = 0, currentPlayer = 1)
        assertTrue(options.isNotEmpty(), "上家打出5万，手中有4万6万，应该可以吃")

        // 对家(player 0)打出，当前玩家(player 2)不可以吃
        val options2 = ruleEngine.canChi(hand, discardTile, fromPlayer = 0, currentPlayer = 2)
        assertTrue(options2.isEmpty(), "非上家不可以吃")
    }

    @Test
    fun `字牌不能吃`() {
        val hand = PlayerHand()
        hand.closedTiles.addAll(listOf(
            Tile(Suit.WIND, 2), Tile(Suit.WIND, 3)
        ))
        val discardTile = Tile(Suit.WIND, 1) // 东风
        val options = ruleEngine.canChi(hand, discardTile, fromPlayer = 0, currentPlayer = 1)
        assertTrue(options.isEmpty(), "字牌不能吃")
    }

    @Test
    fun `碰的合法性`() {
        val hand = PlayerHand()
        hand.closedTiles.addAll(listOf(
            Tile(Suit.MAN, 5), Tile(Suit.MAN, 5, 1)
        ))
        assertTrue(ruleEngine.canPon(hand, Tile(Suit.MAN, 5, 2)), "有2张相同牌可以碰")
        assertFalse(ruleEngine.canPon(hand, Tile(Suit.MAN, 6)), "只有1张不能碰")
    }

    // ============================================================
    // 第七章：振听判定
    // ============================================================

    @Test
    fun `舍牌振听`() {
        val hand = PlayerHand()
        // 听 3万 和 6万
        hand.closedTiles.addAll(listOf(
            Tile(Suit.MAN, 1), Tile(Suit.MAN, 2), Tile(Suit.MAN, 3),
            Tile(Suit.MAN, 4), Tile(Suit.MAN, 5),
            Tile(Suit.SOU, 7), Tile(Suit.SOU, 8), Tile(Suit.SOU, 9),
            Tile(Suit.PIN, 4), Tile(Suit.PIN, 4, 1), Tile(Suit.PIN, 4, 2),
            Tile(Suit.PIN, 5), Tile(Suit.PIN, 5, 1)
        ))
        // 牌河中有 3万 → 振听
        hand.discards.add(Tile(Suit.MAN, 3, 1))
        assertTrue(ruleEngine.isFuriten(hand, hand.closedTiles), "牌河中有待牌应为振听")
    }

    // ============================================================
    // 第十一章：Dora 系统
    // ============================================================

    @Test
    fun `Dora指示牌循环 - 数牌`() {
        // 8万指示 → Dora是9万
        assertEquals(8, Tile(Suit.MAN, 8).doraIndicatorTarget()) // 9万=typeIndex 8
        // 9万指示 → Dora是1万
        assertEquals(0, Tile(Suit.MAN, 9).doraIndicatorTarget()) // 1万=typeIndex 0
    }

    @Test
    fun `Dora指示牌循环 - 风牌`() {
        // 北(rank=4)指示 → Dora是東(rank=1)
        assertEquals(27, Tile(Suit.WIND, 4).doraIndicatorTarget()) // 東=27
        // 東(rank=1)指示 → Dora是南(rank=2)
        assertEquals(28, Tile(Suit.WIND, 1).doraIndicatorTarget()) // 南=28
    }

    @Test
    fun `Dora指示牌循环 - 三元牌`() {
        // 中(rank=3)指示 → Dora是白(rank=1)
        assertEquals(31, Tile(Suit.DRAGON, 3).doraIndicatorTarget()) // 白=31
        // 白(rank=1)指示 → Dora是發(rank=2)
        assertEquals(32, Tile(Suit.DRAGON, 1).doraIndicatorTarget()) // 發=32
    }

    // ============================================================
    // 第一章：牌种属性
    // ============================================================

    @Test
    fun `牌种分类属性`() {
        // 中张牌
        assertTrue(Tile(Suit.MAN, 5).isSimple)
        assertFalse(Tile(Suit.MAN, 1).isSimple)

        // 幺九牌
        assertTrue(Tile(Suit.MAN, 1).isTerminalOrHonor)
        assertTrue(Tile(Suit.MAN, 9).isTerminalOrHonor)
        assertTrue(Tile(Suit.WIND, 1).isTerminalOrHonor)
        assertTrue(Tile(Suit.DRAGON, 1).isTerminalOrHonor)
        assertFalse(Tile(Suit.MAN, 5).isTerminalOrHonor)

        // 绿牌
        assertTrue(Tile(Suit.SOU, 2).isGreen)
        assertTrue(Tile(Suit.SOU, 3).isGreen)
        assertTrue(Tile(Suit.SOU, 6).isGreen)
        assertTrue(Tile(Suit.DRAGON, 2).isGreen) // 發
        assertFalse(Tile(Suit.SOU, 1).isGreen)
        assertFalse(Tile(Suit.SOU, 5).isGreen)
    }

    @Test
    fun `34维索引正确性`() {
        assertEquals(0, Tile(Suit.MAN, 1).typeIndex)
        assertEquals(8, Tile(Suit.MAN, 9).typeIndex)
        assertEquals(9, Tile(Suit.SOU, 1).typeIndex)
        assertEquals(17, Tile(Suit.SOU, 9).typeIndex)
        assertEquals(18, Tile(Suit.PIN, 1).typeIndex)
        assertEquals(26, Tile(Suit.PIN, 9).typeIndex)
        assertEquals(27, Tile(Suit.WIND, 1).typeIndex) // 東
        assertEquals(30, Tile(Suit.WIND, 4).typeIndex) // 北
        assertEquals(31, Tile(Suit.DRAGON, 1).typeIndex) // 白
        assertEquals(33, Tile(Suit.DRAGON, 3).typeIndex) // 中
    }

    @Test
    fun `136张牌物理守恒`() {
        val allTiles = TilePool.buildStandardSet()
        assertEquals(136, allTiles.size, "标准牌集应有136张")

        // 每种牌恰好4张
        val grouped = allTiles.groupBy { it.typeIndex }
        assertEquals(34, grouped.size, "应有34种牌")
        for ((typeIndex, tiles) in grouped) {
            assertEquals(4, tiles.size, "typeIndex=$typeIndex 应有4张")
        }
    }

    // ============================================================
    // 辅助方法
    // ============================================================

    private fun assertNonDealerRon(fu: Int, han: Int, expected: Int) {
        val result = scoreCalc.calculate(fu, han)
        assertEquals(
            expected, result.nonDealerRonPayment(),
            "闲家荣和 ${han}翻${fu}符 应为 $expected 点"
        )
    }

    private fun assertDealerRon(fu: Int, han: Int, expected: Int) {
        val result = scoreCalc.calculate(fu, han)
        assertEquals(
            expected, result.dealerRonPayment(),
            "庄家荣和 ${han}翻${fu}符 应为 $expected 点"
        )
    }
}
