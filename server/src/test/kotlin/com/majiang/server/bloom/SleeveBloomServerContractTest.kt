package com.majiang.server.bloom

import com.majiang.access.ReceiptStatus
import com.majiang.cheat.bloom.*
import com.majiang.economy.CheatEconomyConfig
import com.majiang.match.MatchConfig
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.server.host.ServerTableHost
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.state.GamePhase
import com.majiang.state.SeatController
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import com.majiang.transport.api.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

@Timeout(30)
class SleeveBloomServerContractTest {
    private class Clock(@Volatile var now: Long = 1_000L) : MonotonicClock { override fun nowMillis() = now }
    private fun <T> CompletableFuture<T>.await(): T = get(5, TimeUnit.SECONDS)
    private fun config() = MatchConfig(cheatEconomy = CheatEconomyConfig(true, List(4) { 20 }, mapOf("SLEEVE_BLOOM" to 10)), sleeveBloomConfig = SleeveBloomConfig(true))
    private fun initial() = TestTableBuilder.create(drawn = 0 to Tile(Suit.SOU, 9, 3),
        wallPrefix = listOf(Tile(Suit.PIN, 9, 3)), phase = GamePhase.CHEAT_WINDOW).also {
        it.lastDrawKind = SleeveBloomDrawKind.NORMAL; it.cheatPoints.fill(20)
    }

    @Test fun `serial host maps private two phase state preserves clock and replays both receipts`() {
        val clock = Clock()
        ServerTableHost(config(), clock = clock, controllers = List(4) { SeatController.RemoteHuman(false) }, initialState = initial()).use { host ->
            val before = host.snapshot(0).await()
            val request = SleeveBloomRequest("activate", before.sessionId, before.roundId!!, before.decision.decisionId, before.decision.controlEpoch)
            val first = host.submitSleeveBloom(0, request)
            val competing = host.submitSleeveBloom(0, request.copy(requestId = "competing"))
            assertEquals(ReceiptStatus.APPLIED, first.await().status)
            assertEquals(ReceiptStatus.REJECTED, competing.await().status)
            val offered = host.snapshot(0).await()
            val offer = offered.sleeveBloom!!.pendingOffer!!
            assertEquals(10, offered.cheatPoints[0])
            for (seat in 1..3) assertNull(host.snapshot(seat).await().sleeveBloom!!.pendingOffer)
            val wire = EngineProtocolMapper.toProtocolSnapshot(offered)
            assertEquals(wire.sleeveBloom, ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = wire))).snapshot!!.sleeveBloom)
            val choice = SleeveBloomChoiceRequest("choose", before.sessionId, before.roundId!!,
                offered.decision.decisionId, offered.decision.controlEpoch, offer.offerId, SleeveBloomKeep.SECOND)
            val chosen = host.submitSleeveBloomChoice(0, choice).await()
            assertEquals(ReceiptStatus.APPLIED, chosen.status)
            val after = host.snapshot(0).await()
            assertNull(after.sleeveBloom!!.pendingOffer)
            assertEquals(offer.secondTile, after.view.myDrawnTile)
            assertTrue(after.cursor > offered.cursor, "An eventless selection must advance the snapshot stream")
            assertEquals(before.clocks.first { it.seat == 0 }.deadline, after.clocks.first { it.seat == 0 }.deadline)
            assertEquals(first.await(), host.submitSleeveBloom(0, request).await())
            assertEquals(chosen, host.submitSleeveBloomChoice(0, choice).await())
            assertEquals("REQUEST_ID_CONFLICT", host.submitSleeveBloomChoice(0, choice.copy(keep = SleeveBloomKeep.FIRST)).await().reason)
            assertEquals("REQUEST_ID_CONFLICT", host.submitSleeveBloom(0, request.copy(requestId = choice.requestId)).await().reason)
            assertEquals(after.cursor, host.snapshot(0).await().cursor)
            assertEquals(config().sleeveBloomConfig, EngineProtocolMapper.toEngineConfig(EngineProtocolMapper.toProtocolConfig(config())).sleeveBloomConfig)
        }
    }

    @Test fun `off-turn host wiring keeps private offer reservation and reconnection snapshot`() {
        val clock = Clock()
        val state = TestTableBuilder.create(
            discards = mapOf(0 to listOf(Tile(Suit.MAN, 1, 0))),
            phase = GamePhase.DRAW,
            currentPlayer = 1
        ).also {
            it.cheatPoints.fill(20)
            it.openSleeveBloomWindow(0)
        }
        ServerTableHost(config(), clock = clock, controllers = List(4) { SeatController.RemoteHuman(false) }, initialState = state).use { host ->
            val before = host.snapshot(0).await()
            assertTrue(before.sleeveBloom!!.canApplyOffTurn)
            assertEquals(state.sleeveBloomWindowId(0), before.sleeveBloom!!.offTurnWindowId)
            val request = SleeveBloomOffTurnRequest(
                "off-turn", before.sessionId, before.roundId!!,
                before.sleeveBloom!!.offTurnWindowId!!, before.sleeveBloom!!.controlEpoch
            )
            val applied = host.submitSleeveBloomOffTurn(0, request).await()
            assertEquals(ReceiptStatus.APPLIED, applied.status, applied.reason)
            val offered = host.snapshot(0).await()
            val offer = offered.sleeveBloom!!.pendingOffer!!
            assertEquals(ProtocolSleeveBloomOfferMode.OFF_TURN, EngineProtocolMapper.toProtocolSnapshot(offered).sleeveBloom!!.pendingOffer!!.mode)
            for (seat in 1..3) assertNull(host.snapshot(seat).await().sleeveBloom!!.pendingOffer)
            val wire = EngineProtocolMapper.toProtocolSnapshot(offered)
            val decoded = ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = wire))).snapshot!!
            assertEquals(wire.sleeveBloom, decoded.sleeveBloom)

            val choice = SleeveBloomOffTurnChoiceRequest(
                "off-choice", offered.sessionId, offered.roundId!!,
                offered.sleeveBloom!!.controlEpoch, offer.offerId, SleeveBloomKeep.SECOND
            )
            assertEquals(ReceiptStatus.APPLIED, host.submitSleeveBloomOffTurnChoice(0, choice).await().status)
            val reserved = host.snapshot(0).await()
            assertNull(reserved.sleeveBloom!!.pendingOffer)
            assertEquals(offer.secondTile, reserved.sleeveBloom!!.reservation!!.tile)
            assertEquals(10, reserved.cheatPoints[0])
            assertEquals(applied, host.submitSleeveBloomOffTurn(0, request).await())
            assertEquals("REQUEST_ID_CONFLICT",
                host.submitSleeveBloomOffTurnChoice(0, choice.copy(keep = SleeveBloomKeep.FIRST)).await().reason)
            // Reconnect/resume always receives the complete private reservation, not a delta guess.
            val resumed = host.snapshot(0).await()
            assertEquals(reserved.sleeveBloom!!.reservation!!.tile, resumed.sleeveBloom!!.reservation!!.tile)
            assertEquals(reserved.sleeveBloom!!.reservation!!.offerId, resumed.sleeveBloom!!.reservation!!.offerId)
            assertTrue(resumed.cursor >= reserved.cursor)
        }
    }

    @Test fun `off-turn authority deadline settles first through host tick without refund`() {
        val clock = Clock()
        val state = TestTableBuilder.create(
            discards = mapOf(0 to listOf(Tile(Suit.MAN, 1, 0))),
            phase = GamePhase.DRAW,
            currentPlayer = 1
        ).also {
            it.cheatPoints.fill(20)
            it.openSleeveBloomWindow(0)
        }
        ServerTableHost(config(), clock = clock, controllers = List(4) { SeatController.RemoteHuman(false) }, initialState = state).use { host ->
            val before = host.snapshot(0).await()
            val request = SleeveBloomOffTurnRequest(
                "off-timeout", before.sessionId, before.roundId!!,
                before.sleeveBloom!!.offTurnWindowId!!, before.sleeveBloom!!.controlEpoch
            )
            assertEquals(ReceiptStatus.APPLIED, host.submitSleeveBloomOffTurn(0, request).await().status)
            val offer = host.snapshot(0).await().sleeveBloom!!.pendingOffer!!
            clock.now = offer.deadlineMillis
            assertTrue(host.tick().await())
            val after = host.snapshot(0).await()
            assertNull(after.sleeveBloom!!.pendingOffer)
            assertEquals(offer.firstTile, after.sleeveBloom!!.reservation!!.tile)
            assertEquals(10, after.cheatPoints[0])
        }
    }

    @Test fun `binding validity and deadline are evaluated at queued execution`() {        val clock = Clock()
        val executor = Executors.newSingleThreadExecutor()
        val release = CountDownLatch(1)
        val host = ServerTableHost(config(), clock = clock, controllers = List(4) { SeatController.RemoteHuman(false) },
            initialState = initial(), serialExecutor = executor)
        try {
            val before = host.snapshot(0).await()
            val request = SleeveBloomRequest("binding", before.sessionId, before.roundId!!, before.decision.decisionId, before.decision.controlEpoch)
            val entered = CountDownLatch(1)
            executor.execute { entered.countDown(); check(release.await(5, TimeUnit.SECONDS)) }
            assertTrue(entered.await(3, TimeUnit.SECONDS))
            val valid = AtomicBoolean(true)
            val stale = host.submitSleeveBloom(0, request) { valid.get() }
            valid.set(false)
            clock.now = before.clocks.first { it.seat == 0 }.deadline
            val late = host.submitSleeveBloom(0, request.copy(requestId = "late"))
            release.countDown()
            assertEquals("STALE_SESSION", stale.await().reason)
            assertEquals("DEADLINE_EXPIRED", late.await().reason)
            assertNull(host.snapshot(0).await().sleeveBloom!!.pendingOffer)
            assertEquals(20, host.snapshot(0).await().cheatPoints[0])
        } finally { release.countDown(); host.close(); executor.shutdownNow() }
    }
}
