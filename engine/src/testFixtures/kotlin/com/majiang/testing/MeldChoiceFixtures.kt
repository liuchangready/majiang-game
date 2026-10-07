package com.majiang.testing

import com.majiang.application.ApplicationCoordinator
import com.majiang.event.GameEventBus
import com.majiang.model.*
import com.majiang.session.PlayerView
import com.majiang.state.*

object MeldChoiceFixtures {
    fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { c -> val n = c.digitToInt(); Tile(suit,n,copies.getOrDefault(n,0)).also { copies[n]=it.copyIndex+1 } }
    }
    data class Fixture(val coordinator: ApplicationCoordinator, val seat: Int = 2) {
        val engine: ApplicationCoordinator get() = coordinator
        val state: GameState get() = coordinator.activeRound?.readState() ?: coordinator.lastEndedRoundSnapshot ?: GameState()
        fun view() = PlayerView.fromGameState(
            coordinator.activeRound?.readState() ?: coordinator.lastEndedRoundSnapshot ?: GameState(), seat, coordinator.legalActions(seat))
        fun passOthers() { for (p in 0..3) if (p != seat && GameAction.Pass(p) in coordinator.legalActions(p)) coordinator.handleAction(GameAction.Pass(p)) }
    }
    private fun response(hand: List<Tile>, tile: Tile): Fixture {
        val s = TestTableBuilder.create(hands=mapOf(2 to hand), drawn=1 to tile)
        val c = ApplicationCoordinator.createForTest(s,GameEventBus(),autoPlayAi=false,autoRiichiDiscard=false)
        c.initializeMatch(2)
        check(c.handleAction(GameAction.DiscardTile(1,tile)) is ActionResult.Accepted)
        return Fixture(c)
    }
    fun chi() = response(tiles(Suit.MAN,"3467")+tiles(Suit.PIN,"123")+tiles(Suit.SOU,"123")+tiles(Suit.DRAGON,"111"), Tile(Suit.MAN,5))
    fun ponRon() = response(tiles(Suit.DRAGON,"11")+tiles(Suit.MAN,"234")+tiles(Suit.PIN,"234")+tiles(Suit.SOU,"23455"), Tile(Suit.DRAGON,1,2))
    fun openKan() = response(tiles(Suit.DRAGON,"111")+tiles(Suit.MAN,"234")+tiles(Suit.PIN,"678")+tiles(Suit.SOU,"2256"), Tile(Suit.DRAGON,1,3))
    fun ownKans(): Fixture {
        val ponTiles = tiles(Suit.PIN,"555")
        val pon = Meld(MeldType.PON,ponTiles,0,ponTiles[0])
        val s = TestTableBuilder.create(hands=mapOf(2 to (tiles(Suit.MAN,"1111")+tiles(Suit.PIN,"67")+tiles(Suit.SOU,"234")+tiles(Suit.WIND,"1"))),
            melds=mapOf(2 to listOf(pon)), drawn=2 to Tile(Suit.PIN,5,3))
        val c = ApplicationCoordinator.createForTest(s,GameEventBus(),autoPlayAi=false,autoRiichiDiscard=false)
        c.initializeMatch(2)
        return Fixture(c)
    }
}
