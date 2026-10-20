package com.majiang.ai

import com.majiang.model.Meld
import com.majiang.model.MeldType
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.session.PlayerView
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * 三类目标感知实验 AI 的确定性、合法性与基本目标行为。
 *
 * 这些策略只用于可复现实验夹具，不是权威规则。
 */
class TargetAwareAiStrategiesTest {

    private fun t(suit: Suit, rank: Int, copy: Int = 0) = Tile(suit, rank, copy)

    private fun view(
        closed: List<Tile>,
        drawn: Tile? = null,
        legal: List<GameAction>,
        melds: List<Meld> = emptyList(),
        lastDiscard: Tile? = null,
        riichi: Boolean = false
    ): PlayerView {
        val allMelds = List(4) { seat -> if (seat == 0) melds else emptyList() }
        return PlayerView.create(
            viewerSeat = 0,
            phase = GamePhase.DISCARD,
            roundWind = 1,
            roundNumber = 0,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 60,
            doraIndicators = listOf(t(Suit.MAN, 1)),
            scores = List(4) { 25_000 },
            riichiStatus = List(4) { riichi && it == 0 },
            discards = List(4) { emptyList() },
            melds = allMelds,
            handTileCounts = List(4) { closed.size + if (drawn != null) 1 else 0 },
            myClosedTiles = closed,
            myDrawnTile = drawn,
            legalActions = legal,
            lastDiscard = lastDiscard,
            lastDiscardPlayer = if (lastDiscard != null) 1 else null
        )
    }

    private fun discards(vararg tiles: Tile): List<GameAction> =
        tiles.map { GameAction.DiscardTile(0, it) }

    @Test
    fun `suit goal discards off-suit first and only melds its own suit`() {
        val man = listOf(t(Suit.MAN, 1), t(Suit.MAN, 2), t(Suit.MAN, 3), t(Suit.MAN, 5))
        val pin = t(Suit.PIN, 5)
        val sou = t(Suit.SOU, 9)
        val legal = discards(*(man + pin + sou).toTypedArray())
        val strategy = TargetAwareAi.SuitGoalAi(Suit.MAN)
        val chosen = strategy.decideAction(view(man + listOf(pin, sou), legal = legal), legal)
        assertTrue(chosen is GameAction.DiscardTile)
        assertTrue((chosen as GameAction.DiscardTile).tile.suit != Suit.MAN, "must discard off-suit first")

        val souChi = Meld(
            type = MeldType.CHI,
            tiles = listOf(t(Suit.SOU, 1), t(Suit.SOU, 2), t(Suit.SOU, 3)),
            fromPlayer = 1,
            calledTile = t(Suit.SOU, 3)
        )
        val withSouChi = legal + GameAction.DeclareChi(0, souChi)
        val offSuitChosen = strategy.decideAction(
            view(man + listOf(pin, sou), legal = withSouChi, lastDiscard = souChi.calledTile),
            withSouChi
        )
        assertNotEquals(GameAction.DeclareChi(0, souChi), offSuitChosen, "must not chi an off-suit tile")
    }

    @Test
    fun `honor triplet goal keeps target honors and pons the missing triplet`() {
        val hand = listOf(
            t(Suit.DRAGON, 1), t(Suit.DRAGON, 2), t(Suit.DRAGON, 2),
            t(Suit.MAN, 1), t(Suit.MAN, 2), t(Suit.MAN, 3)
        )
        val terminal = t(Suit.PIN, 1)
        val all = hand + terminal
        val legal = discards(*all.toTypedArray())
        val strategy = TargetAwareAi.HonorTripletGoalAi(setOf(31, 32, 33))
        val chosen = strategy.decideAction(view(all, legal = legal), legal)
        assertTrue(chosen is GameAction.DiscardTile)
        assertEquals(Suit.PIN, (chosen as GameAction.DiscardTile).tile.suit, "keep target dragons over a terminal")

        val ponTile = t(Suit.DRAGON, 3)
        val pon = GameAction.DeclarePon(0, listOf(ponTile, ponTile.copy(copyIndex = 1), ponTile.copy(copyIndex = 2)))
        val withPon = legal + pon
        assertEquals(pon, strategy.decideAction(view(all, legal = withPon, lastDiscard = ponTile), withPon))
    }

    @Test
    fun `kokushi skips nine terminals abortive draw and keeps unique terminals`() {
        val terminals = listOf(
            t(Suit.MAN, 1), t(Suit.MAN, 9), t(Suit.SOU, 1), t(Suit.SOU, 9),
            t(Suit.PIN, 1), t(Suit.PIN, 9), t(Suit.WIND, 1), t(Suit.WIND, 2),
            t(Suit.DRAGON, 1)
        )
        val extra = t(Suit.MAN, 5)
        val all = terminals + extra
        val legal = discards(*all.toTypedArray()) + GameAction.DeclareAbortiveDraw(0)
        val chosen = TargetAwareAi.KokushiGoalAi.decideAction(view(all, legal = legal), legal)
        assertTrue(chosen is GameAction.DiscardTile)
        assertEquals(Suit.MAN, (chosen as GameAction.DiscardTile).tile.suit)
        assertEquals(5, chosen.tile.rank, "discard the non-terminal, not a unique kokushi tile")

        // 无打牌、仅禁忌动作时必须返回 null，而不是宣告九种九牌或鸣牌。
        val onlyForbidden = listOf(
            GameAction.DeclareAbortiveDraw(0),
            GameAction.DeclarePon(0, listOf(t(Suit.DRAGON, 1), t(Suit.DRAGON, 1), t(Suit.DRAGON, 1)))
        )
        assertNull(TargetAwareAi.KokushiGoalAi.decideAction(view(terminals, legal = onlyForbidden), onlyForbidden))
    }

    @Test
    fun `all strategies prioritize legal win actions and remain deterministic`() {
        val hand = listOf(t(Suit.MAN, 1), t(Suit.MAN, 2), t(Suit.MAN, 3))
        val legal = listOf(GameAction.DeclareRon(0)) + discards(*hand.toTypedArray())
        val strategies = listOf(
            TargetAwareAi.SuitGoalAi(Suit.MAN),
            TargetAwareAi.HonorTripletGoalAi(setOf(31, 32, 33)),
            TargetAwareAi.KokushiGoalAi
        )
        for (strategy in strategies) {
            val first = strategy.decideAction(view(hand, legal = legal), legal)
            val second = strategy.decideAction(view(hand, legal = legal), legal)
            assertEquals(GameAction.DeclareRon(0), first)
            assertEquals(first, second)
            assertTrue(first in legal)
        }
    }

    @Test
    fun `strategies return only legal actions or null`() {
        val hand = listOf(t(Suit.MAN, 2), t(Suit.SOU, 4), t(Suit.PIN, 7))
        val legal = discards(*(hand + t(Suit.WIND, 3)).toTypedArray())
        val strategies = listOf(
            TargetAwareAi.SuitGoalAi(Suit.PIN),
            TargetAwareAi.HonorTripletGoalAi(setOf(27, 28, 29, 30)),
            TargetAwareAi.KokushiGoalAi
        )
        for (strategy in strategies) {
            val chosen = strategy.decideAction(view(hand, legal = legal), legal)
            assertNotNull(chosen)
            assertTrue(chosen in legal)
        }
        for (strategy in strategies) {
            assertNull(strategy.decideAction(view(hand, legal = emptyList()), emptyList()))
        }
    }

    @Test
    fun `suit goal honors riichi tsumogiri when it is legal`() {
        val closed = listOf(t(Suit.MAN, 1), t(Suit.MAN, 2), t(Suit.MAN, 3))
        val drawn = t(Suit.PIN, 9)
        val legal = discards(drawn, t(Suit.MAN, 1))
        val chosen = TargetAwareAi.SuitGoalAi(Suit.MAN).decideAction(
            view(closed, drawn = drawn, legal = legal, riichi = true),
            legal
        )
        assertEquals(GameAction.DiscardTile(0, drawn), chosen)
    }
}
