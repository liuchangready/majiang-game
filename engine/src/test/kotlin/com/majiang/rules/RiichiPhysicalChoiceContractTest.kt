package com.majiang.rules

import com.majiang.application.ApplicationCoordinator
import com.majiang.event.GameEventBus
import com.majiang.model.*
import com.majiang.state.*
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class RiichiPhysicalChoiceContractTest {
    @Test fun `both identical copies including drawn tile can be selected and submitted`() {
        for (chosen in 0..1) {
            val east0 = Tile(Suit.WIND, 1)
            val east1 = Tile(Suit.WIND, 1, 1)
            val closed = (1..6).map { Tile(Suit.MAN, it) } + (7..9).map { Tile(Suit.PIN, it) } +
                (1..3).map { Tile(Suit.SOU, it) } + east0
            val s = TestTableBuilder.create(hands = mapOf(0 to closed), drawn = 0 to east1)
            val e = ApplicationCoordinator.createForTest(s, GameEventBus(), autoPlayAi = false)
            val actions = e.legalActions(0).filterIsInstance<GameAction.DeclareRiichi>()
            assertTrue(GameAction.DeclareRiichi(0, east0) in actions)
            assertTrue(GameAction.DeclareRiichi(0, east1) in actions)
            val selected = if (chosen == 0) east0 else east1
            assertTrue(e.handleAction(GameAction.DeclareRiichi(0, selected)) is ActionResult.Accepted)
            val live = e.activeRound!!.readState()
            assertEquals(selected, live.players[0].discardHistory.single())
            assertEquals(24000, live.scores[0])
            TestTableBuilder.assertPhysical(live)
        }
    }

    @Test fun `kan ownership filters the impossible fifth tile from riichi eligibility`() {
        val kan = (0..3).map { Tile(Suit.MAN, 1, it) }
        // 23m still has the real 4m side, so this remains a legal riichi choice.
        val closed = listOf(Tile(Suit.MAN, 2), Tile(Suit.MAN, 3)) + (4..6).map { Tile(Suit.PIN, it) } +
            (7..9).map { Tile(Suit.SOU, it) } + listOf(Tile(Suit.WIND, 2), Tile(Suit.WIND, 2, 1))
        val discard = Tile(Suit.DRAGON, 3)
        val s = TestTableBuilder.create(hands = mapOf(0 to closed), drawn = 0 to discard,
            melds = mapOf(0 to listOf(Meld(MeldType.CLOSED_KAN, kan, -1, kan.last()))))
        assertTrue(discard in RiichiRuleEngine.INSTANCE.canRiichi(s.players[0], s, 0))
        TestTableBuilder.assertPhysical(s)
    }
}
