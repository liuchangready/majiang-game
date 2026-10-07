package com.majiang.state

import com.majiang.application.ApplicationCoordinator
import com.majiang.event.GameEvent
import com.majiang.event.GameEventBus
import com.majiang.model.*
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class ScoreBreakdownEventContractTest {

    @Test
    fun `backward compatibility - TsumoWin and RonWin allow null breakdown by default`() {
        val tsumo = GameEvent.TsumoWin(
            turnNumber = 1,
            player = 0,
            winTile = Tile(Suit.MAN, 1),
            yakuList = listOf("立直 1翻"),
            han = 1,
            fu = 30,
            score = 1000
        )
        assertNull(tsumo.breakdown, "未传 breakdown 时应默认为 null")

        val ron = GameEvent.RonWin(
            turnNumber = 1,
            winner = 0,
            loser = 1,
            winTile = Tile(Suit.MAN, 1),
            yakuList = listOf("立直 1翻"),
            han = 1,
            fu = 30,
            score = 1000
        )
        assertNull(ron.breakdown, "未传 breakdown 时应默认为 null")
    }

    @Test
    fun `tsumo win event carries full score breakdown in GameStateMachine`() {
        val hand = listOf(
            Tile(Suit.MAN, 2), Tile(Suit.MAN, 3), Tile(Suit.MAN, 4),
            Tile(Suit.PIN, 2), Tile(Suit.PIN, 3), Tile(Suit.PIN, 4),
            Tile(Suit.SOU, 2), Tile(Suit.SOU, 3), Tile(Suit.SOU, 4),
            Tile(Suit.SOU, 6), Tile(Suit.SOU, 7), Tile(Suit.SOU, 8), Tile(Suit.MAN, 5, 3) // 红5万
        )
        val dora = Tile(Suit.MAN, 4, 2) // 指示 5万
        val state = TestTableBuilder.create(
            hands = mapOf(0 to hand),
            drawn = 0 to Tile(Suit.MAN, 5, 0), // 普通5万
            dealer = 0,
            deadWallPrefix = listOf(dora)
        ).also {
            it.doraIndicators.add(dora)
            it.honbaSticks = 1
            it.riichiSticks = 1
        }

        val bus = GameEventBus()
        val engine = ApplicationCoordinator.createForTest(state, bus, autoPlayAi = false)
        val res = engine.handleAction(GameAction.DeclareTsumo(0))
        assertTrue(res is ActionResult.Accepted)

        val win = bus.getHistory().filterIsInstance<GameEvent.TsumoWin>().single()
        val bd = win.breakdown
        assertNotNull(bd, "GameStateMachine 自摸和牌必须填充 breakdown 字段")
        assertEquals(bd!!.dora.indicatorCount, 2)
        assertEquals(bd.dora.redCount, 1)
        assertEquals(bd.dora.totalHan, 3)
        assertTrue(bd.yakuLines.isNotEmpty())
        assertEquals(300, bd.honbaAddition)
        assertEquals(1000, bd.riichiAddition)
        assertTrue(bd.paymentLines.isNotEmpty())
        assertEquals(bd.score.totalPoints(isDealer = true, isTsumo = true) + 1300, bd.totalPoints)
    }

    @Test
    fun `ron win event carries full score breakdown in GameStateMachine`() {
        val winnerHand = listOf(
            Tile(Suit.MAN, 2), Tile(Suit.MAN, 3), Tile(Suit.MAN, 4),
            Tile(Suit.PIN, 2), Tile(Suit.PIN, 3), Tile(Suit.PIN, 4),
            Tile(Suit.SOU, 2), Tile(Suit.SOU, 3), Tile(Suit.SOU, 4),
            Tile(Suit.SOU, 6), Tile(Suit.SOU, 7), Tile(Suit.SOU, 8), Tile(Suit.MAN, 5, 3)
        )
        val dora = Tile(Suit.MAN, 4, 1)
        val discardTile = Tile(Suit.MAN, 5, 0)
        val state = TestTableBuilder.create(
            hands = mapOf(0 to winnerHand),
            drawn = 1 to discardTile,
            currentPlayer = 1,
            phase = GamePhase.DISCARD,
            dealer = 0,
            deadWallPrefix = listOf(dora)
        ).also {
            it.doraIndicators.add(dora)
            it.honbaSticks = 2
            it.riichiSticks = 0
        }

        val bus = GameEventBus()
        val engine = ApplicationCoordinator.createForTest(state, bus, autoPlayAi = false)
        assertTrue(engine.handleAction(GameAction.DiscardTile(1, discardTile)) is ActionResult.Accepted)

        for (seat in listOf(2, 3)) {
            if (GameAction.Pass(seat) in engine.legalActions(seat)) {
                engine.handleAction(GameAction.Pass(seat))
            }
        }
        assertTrue(engine.handleAction(GameAction.DeclareRon(0)) is ActionResult.Accepted)

        val win = bus.getHistory().filterIsInstance<GameEvent.RonWin>().single()
        val bd = win.breakdown
        assertNotNull(bd, "GameStateMachine 荣和和牌必须填充 breakdown 字段")
        assertEquals(1, win.loser)
        assertEquals(600, bd!!.honbaAddition)
        assertEquals(0, bd.riichiAddition)
        assertTrue(bd.paymentLines.any { it.contains("P1") })
        assertEquals(bd.dora.indicatorCount, 2)
        assertEquals(bd.dora.redCount, 1)
    }
}
