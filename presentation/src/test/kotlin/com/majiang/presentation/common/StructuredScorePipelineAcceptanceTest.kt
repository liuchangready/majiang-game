package com.majiang.presentation.common

import com.majiang.client.api.PublicSnapshotView
import com.majiang.client.api.PublicSessionSummaryView
import com.majiang.client.api.GameEventView
import com.majiang.client.mapping.ProtocolViewMapper
import com.majiang.projection.PublicProjectionFactory
import com.majiang.event.GameEvent
import com.majiang.model.PlayerHand
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.rules.ScoreBreakdown
import com.majiang.rules.WinningScoreEvaluator
import com.majiang.rules.YakuEvaluator
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ProtocolScoreBreakdown
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * D-07 全链验收：engine → server mapper → protocol JSON → client mapper/view →
 * presentation，覆盖玩家私有结算与观战公开结算。
 */
class StructuredScorePipelineAcceptanceTest {

    private fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { c ->
            val rank = c.digitToInt()
            Tile(suit, rank, copies.getOrDefault(rank, 0)).also { copies[rank] = it.copyIndex + 1 }
        }
    }

    private fun pinfuHand() = PlayerHand().apply {
        closedTiles.addAll(tiles(Suit.MAN, "23") + tiles(Suit.PIN, "234567") + tiles(Suit.SOU, "23488"))
    }

    private fun engineBreakdown(): ScoreBreakdown =
        WinningScoreEvaluator().explain(
            hand = pinfuHand(),
            context = YakuEvaluator.WinContext(
                Tile(Suit.MAN, 1),
                isTsumo = false,
                isClosed = true,
                seatWind = 2,
                roundWind = 1
            ),
            doraIndicators = emptyList(),
            honba = 2,
            riichiSticks = 1,
            loser = 3
        )!!

    private fun wireRoundtrip(proto: ProtocolScoreBreakdown): ProtocolScoreBreakdown {
        val json = ApiMessageCodec.gson.toJson(proto)
        return ApiMessageCodec.gson.fromJson(json, ProtocolScoreBreakdown::class.java)
    }

    @Test
    fun `player settlement preserves every numeric and payment value through the full chain`() {
        val engine = engineBreakdown()
        val proto = wireRoundtrip(EngineProtocolMapper.toProtocolScoreBreakdown(engine))
        val view = ProtocolViewMapper.mapScoreBreakdown(proto)

        // 数值逐项相等。
        assertEquals(engine.score.fu, view.score.fu)
        assertEquals(engine.score.han, view.score.han)
        assertEquals(engine.score.basePoints, view.score.basePoints)
        assertEquals(engine.score.rawBasePoints, view.score.rawBasePoints)
        assertEquals(engine.score.rawBasePointsExact, view.score.rawBasePointsExact)
        assertEquals(engine.totalPoints, view.totalPoints)
        assertEquals(engine.honbaAddition, view.honbaAddition)
        assertEquals(engine.riichiAddition, view.riichiAddition)
        assertEquals(engine.yakuLines, view.yakuLines)
        assertEquals(engine.paymentLines, view.paymentLines)
        assertEquals(engine.uraEligible, view.uraEligible)

        // 结构化字段逐项相等，且支付计划与基础总额守恒。
        assertEquals(engine.yakuEntries.map { Triple(it.code, it.displayName, it.han) },
            view.yakuEntries.map { Triple(it.code, it.displayName, it.han) })
        assertEquals(engine.paymentPlan.kind.code, view.paymentPlan!!.kind)
        assertEquals(engine.paymentPlan.total, view.paymentPlan!!.total)
        assertEquals(
            engine.paymentPlan.components.map { Triple(it.payerRole.code, it.amount, it.count) },
            view.paymentPlan!!.components.map { Triple(it.payerRole, it.amount, it.count) }
        )
        assertEquals(
            view.totalPoints,
            view.paymentPlan!!.total + view.honbaAddition + view.riichiAddition
        )

        // presentation 使用结构化计划金额；普通分/本场/立直/总得点全部出现在计算页。
        val rows = SettlementDetailPresenter.rows(view, SettlementSection.CALC)
        val text = rows.joinToString("\n") { it.text }
        assertTrue(text.contains("放铳者 支付 ${engine.paymentPlan.total} 点"), text)
        assertTrue(text.contains("计划合计 ${engine.paymentPlan.total} 点"), text)
        assertTrue(text.contains("本场"), text)
        assertTrue(text.contains("供托立直棒"), text)
        assertTrue(text.contains(engine.totalPoints.toString()), text)
        assertFalse(text.contains("基础支付分配：${engine.paymentLines.joinToString("; ")}"), text)
    }

    @Test
    fun `spectator public settlement uses structured projection without private leaks`() {
        val engine = engineBreakdown()
        val winner = 3
        val event = GameEvent.RonWin(
            turnNumber = 12,
            winner = winner,
            loser = 0,
            winTile = Tile(Suit.MAN, 1),
            yakuList = engine.yakuLines.map { it.first },
            han = engine.score.han,
            fu = engine.score.fu,
            score = engine.totalPoints,
            breakdown = engine
        )
        val publicEvents = PublicProjectionFactory.publicEvents(
            listOf(event),
            revealSettlementUra = false
        )
        assertEquals(1, publicEvents.size)
        val protocolEvent = EngineProtocolMapper.toProtocolEvent(publicEvents.single())
        val viewEvent = ProtocolViewMapper.mapGameEvent(protocolEvent)
        val snapshot = PublicSnapshotView(
            sessionId = "session-d07",
            sessionVersion = 2L,
            syncEpoch = "epoch-d07",
            cursor = 7L,
            session = PublicSessionSummaryView(
                lifecycle = "ROUND_RESULT",
                scores = listOf(25000, 25000, 25000, 25000),
                roundWind = 1,
                roundNumber = 1,
                dealerSeat = 0,
                honba = 2
            ),
            settlementRoundId = "round-d07",
            settlementEvents = listOf(viewEvent)
        )

        val breakdown = (viewEvent as GameEventView.RonWin).breakdown
        assertNotNull(breakdown)
        assertEquals(engine.paymentPlan.total, breakdown!!.paymentPlan!!.total)
        assertEquals(engine.yakuEntries.size, breakdown.yakuEntries.size)

        // 未授权里宝：公开投影剥除里宝牌面与命中，结构化字段不得绕过该边界。
        assertTrue(breakdown.dora.uraTiles.isEmpty(), "public projection must not expose ura tiles")
        assertTrue(breakdown.dora.uraHits.isEmpty(), "public projection must not expose ura hits")

        // 公开事件白名单：结算只包含 RonWin，不含私有千术账本事件。
        assertTrue(snapshot.settlementEvents.all { it is com.majiang.client.api.GameEventView.RonWin })

        val rows = SettlementDetailPresenter.rows(breakdown, SettlementSection.DORA)
        val doraText = rows.joinToString("\n") { it.text }
        assertFalse(doraText.contains("里指示牌 1"), doraText)
        val calcText = SettlementDetailPresenter.rows(breakdown, SettlementSection.CALC)
            .joinToString("\n") { it.text }
        assertTrue(calcText.contains("计划合计 ${engine.paymentPlan.total} 点"), calcText)
    }
}
