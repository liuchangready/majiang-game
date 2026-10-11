package com.majiang.cheat.mark

import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.authority.TableAuthority
import com.majiang.economy.CheatEconomyConfig
import com.majiang.event.GameEvent
import com.majiang.match.MatchConfig
import com.majiang.match.SessionLifecycle
import com.majiang.state.SeatController
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** A subscriber may release its authority in response to an update; it must not upgrade its own read lock. */
class TileMarkCloseCallbackTest {
    @Test
    fun `closing authority from a fee subscriber never deadlocks marking publication`() {
        val clock = object : MonotonicClock { override fun nowMillis() = 1_000L }
        val a = TableAuthority(
            config = MatchConfig(
                cheatEconomy = CheatEconomyConfig(true, List(4) { 40 }, mapOf("TILE_MARK" to 10)),
                tileMarkConfig = TileMarkConfig(true)),
            clock = clock,
            controllers = List(4) { SeatController.RemoteHuman(false) },
            initialState = TestTableBuilder.create().also { it.cheatPoints.fill(40) })
        a.syncDecisionsAndClocks(clock.nowMillis(), emptySet())
        val snapshot = a.querySnapshot(0, "test-sync", 0L, List(4) { SeatControlView(it, true, "HUMAN", 0L) })
        val ids = (snapshot.view.myClosedTiles + listOfNotNull(snapshot.view.myDrawnTile)).take(4).map { it.tileId }
        val request = TileMarkRequest("mark-close", snapshot.sessionId, snapshot.roundId!!,
            snapshot.decision.decisionId, snapshot.decision.controlEpoch, ids)
        a.coordinator.subscribe { events ->
            if (events.any { it is GameEvent.CheatPointsChanged }) a.close()
        }
        val completion = CompletableFuture<ReceiptStatus>()
        Thread({
            try { completion.complete(a.submitTileMark(0, request, clock.nowMillis()).receipt.status) }
            catch (failure: Throwable) { completion.completeExceptionally(failure) }
        }, "tile-mark-close-regression").apply {
            // If the regression recurs, an uninterruptible lock must not trap the test worker JVM.
            isDaemon = true
            start()
        }
        assertEquals(ReceiptStatus.APPLIED, completion.get(3, TimeUnit.SECONDS))
        assertEquals(SessionLifecycle.CLOSED, a.sessionLifecycle)
        assertTrue(a.coordinator.session.tileMarkState.groups.isEmpty())
        assertNull(a.decisionClock.timing(0), "Successful return must not recreate a clock after close")
        assertEquals(ReceiptStatus.REJECTED, a.submitTileMark(0, request, clock.nowMillis()).receipt.status,
            "Closed authority must not retain the just-completed success receipt")
        a.close()
        assertTrue(a.coordinator.session.tileMarkState.groups.isEmpty())
    }
}
