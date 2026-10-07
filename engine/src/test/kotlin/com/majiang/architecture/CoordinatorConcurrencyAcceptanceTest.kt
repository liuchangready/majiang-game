package com.majiang.architecture

import com.majiang.ai.AIStrategy
import com.majiang.application.ApplicationCoordinator
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.session.PlayerView
import com.majiang.state.*
import com.majiang.testing.TestTableBuilder
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class CoordinatorConcurrencyAcceptanceTest {
    @Test fun `a concurrent query observes the completed batch and never an intermediate AI turn`() {
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val readerStarted = CountDownLatch(1)
        val strategy = object : AIStrategy {
            override val name = "query consistency barrier"
            override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? {
                entered.countDown()
                check(release.await(5, TimeUnit.SECONDS))
                return legalActions.firstOrNull { it is GameAction.DiscardTile }
            }
        }
        val loose = listOf(1, 5, 9).map { Tile(Suit.MAN, it) } +
            listOf(1, 3, 9).map { Tile(Suit.SOU, it) } +
            listOf(1, 5, 9).map { Tile(Suit.PIN, it) } +
            (1..3).map { Tile(Suit.WIND, it) } + Tile(Suit.DRAGON, 3)
        val state = TestTableBuilder.create(hands = mapOf(1 to loose), phase = GamePhase.DRAW,
            currentPlayer = 1, wallPrefix = listOf(Tile(Suit.WIND, 4)))
        val c = ApplicationCoordinator(initialState = state)
        c.initializeMatch(List(4) { if (it == 1) SeatController.Computer(strategy) else SeatController.RemoteHuman(false) })
        val threads = Executors.newFixedThreadPool(2)
        try {
            val write = threads.submit<ActionResult> { c.handleAction(GameAction.DrawTile(1)) }
            assertTrue(entered.await(3, TimeUnit.SECONDS))
            val read = threads.submit<PlayerView> { readerStarted.countDown(); c.queryView(0) }
            assertTrue(readerStarted.await(1, TimeUnit.SECONDS))
            assertThrows(TimeoutException::class.java) { read.get(150, TimeUnit.MILLISECONDS) }
            release.countDown()
            assertTrue(write.get(5, TimeUnit.SECONDS) is ActionResult.Accepted)
            val view = read.get(5, TimeUnit.SECONDS)
            assertEquals(c.queryView(0), view)
            assertEquals(1, view.discards[1].size)
        } finally {
            release.countDown()
            threads.shutdownNow()
            assertTrue(threads.awaitTermination(5, TimeUnit.SECONDS))
        }
    }
}
