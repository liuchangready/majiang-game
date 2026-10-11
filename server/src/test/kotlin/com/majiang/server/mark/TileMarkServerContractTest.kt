package com.majiang.server.mark

import com.majiang.access.ReceiptStatus
import com.majiang.cheat.mark.TileMarkConfig
import com.majiang.cheat.mark.TileMarkRequest
import com.majiang.economy.CheatEconomyConfig
import com.majiang.match.MatchConfig
import com.majiang.server.api.*
import com.majiang.server.host.ServerTableHost
import com.majiang.server.inprocess.InProcessServer
import com.majiang.server.mapping.EngineProtocolMapper
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

/** Codex A2 integration tests for real bindings and the authority queue. */
@Timeout(25)
class TileMarkServerContractTest {
    private fun <T> CompletableFuture<T>.await(): T = get(5, TimeUnit.SECONDS)
    private fun config() = MatchConfig(
        cheatEconomy = CheatEconomyConfig(true, List(4) { 40 }, mapOf("TILE_MARK" to 10)),
        tileMarkConfig = TileMarkConfig(true))
    private fun request(snapshot: ProtocolPlayerSnapshot, id: String = "mark") = ProtocolTileMarkRequest(
        id, snapshot.sessionId, snapshot.roundId!!, snapshot.decision.decisionId, snapshot.decision.controlEpoch,
        (snapshot.view.myClosedTiles + listOfNotNull(snapshot.view.myDrawnTile)).take(4).map { it.tileId })

    @Test
    fun `local binding pushes private marks charges once preserves clock and records a marked discard`() {
        InProcessServer().use { server ->
            val connection = server.createLocalTable(config(), 0)
            assertTrue(connection.capabilities().await().tileMarkSupported)
            assertEquals(ProtocolReceiptStatus.APPLIED, connection.start().await().status)
            val before = connection.snapshot().await()
            assertTrue(before.tileMark!!.canApply)
            val request = request(before)
            val pushed = CompletableFuture<ProtocolPlayerSnapshot>()
            connection.onUpdate { update ->
                if (update.snapshot.tileMark?.activeTileCount == 4) pushed.complete(update.snapshot)
            }.use {
                val receipt = connection.submitTileMark(request).await()
                assertEquals(ProtocolReceiptStatus.APPLIED, receipt.status)
                val update = pushed.await()
                assertEquals(receipt.cursor, update.cursor)
                assertEquals(30, update.cheatPoints[0])
                assertEquals(request.tileIds.toSet(), update.tileMark!!.activeGroups.single().tiles.map { it.tileId }.toSet())
                val oldClock = before.clocks.first { it.seat == 0 }
                val newClock = update.clocks.first { it.seat == 0 }
                assertEquals(oldClock.deadline, newClock.deadline)
                assertEquals(oldClock.opensAt, newClock.opensAt)
                assertEquals(oldClock.reserve, newClock.reserve)
                assertNotEquals(before.decision.decisionId, update.decision.decisionId)
                assertEquals(receipt, connection.submitTileMark(request.copy(tileIds = request.tileIds.reversed())).await())
                assertEquals(update.cursor, connection.snapshot().await().cursor)

                val option = update.decision.options.first { opt ->
                    val action = opt.action
                    action is ProtocolLegalAction.DiscardTile && action.tile.tileId == request.tileIds.first()
                }
                val discard = ProtocolActionChoice("discard", update.roundId, update.decision.decisionId,
                    update.decision.controlEpoch, option.optionId)
                assertNotEquals(ProtocolReceiptStatus.REJECTED, connection.submit(discard).await().status)
                val after = connection.snapshot().await()
                val tile = after.tileMark!!.activeGroups.single().tiles.single { it.tileId == request.tileIds.first() }
                assertEquals(ProtocolTileMarkArea.DISCARD, tile.area)
                assertEquals(ProtocolRiverPosition(after.roundId!!, 0, 0), tile.riverPosition)
                assertEquals(tile.tileId, after.view.discards[0][0].tileId)
                assertEquals(after.tileMark, connection.resume(0).await().tileMark)
            }
            connection.close()
            assertEquals("SESSION_CLOSED", connection.submitTileMark(request).await().reason)
        }
    }

    @Test
    fun `private effect survives binding replacement while guest and spectator receive no owner marks`() {
        InProcessServer(isSingleplayer = false).use { server ->
            val host = server.createConnection()
            val bound = host.createRoom(CreateRoomRequest("Host", EngineProtocolMapper.toProtocolConfig(config()))).await()
            val guest = server.createConnection()
            guest.joinRoom(JoinRoomRequest(bound.roomId, playerName = "Guest")).await()
            val spectator = server.createConnection()
            spectator.joinSpectator(JoinSpectatorRequest(bound.roomId, "Observer")).await()
            host.setReady(true).await()
            guest.setReady(true).await()
            assertEquals(ProtocolReceiptStatus.APPLIED, host.start().await().status)
            val before = host.snapshot().await()
            val request = request(before)
            assertEquals(ProtocolReceiptStatus.APPLIED, host.submitTileMark(request).await().status)
            val marked = host.snapshot().await()
            assertEquals(4, marked.tileMark!!.activeTileCount)
            assertTrue(guest.snapshot().await().tileMark!!.activeGroups.isEmpty())
            assertEquals("SPECTATOR_READ_ONLY", spectator.submitTileMark(request).await().reason)
            val publicJson = ApiMessageCodec.gson.toJson(spectator.publicSnapshot().await())
            assertFalse(publicJson.contains("tileMark"))
            assertFalse(publicJson.contains(marked.tileMark!!.activeGroups.single().groupId))
            val replacement = server.createConnection()
            val resumed = replacement.resumeSession(ResumeRequest(bound.roomId, bound.resumeToken)).await()
            assertEquals(bound.seat, resumed.seat)
            val restored = replacement.snapshot().await()
            assertEquals(marked.tileMark!!.activeGroups, restored.tileMark!!.activeGroups)
            assertEquals(30, restored.cheatPoints[0])
            assertEquals(marked.clocks.first { it.seat == 0 }.deadline, restored.clocks.first { it.seat == 0 }.deadline)
            assertEquals(restored.tileMark, replacement.resume(0).await().tileMark)
        }
    }

    private class ManualClock(@Volatile var now: Long = 1_000L) : MonotonicClock {
        override fun nowMillis() = now
    }

    @Test
    fun `serial host allows only one of two commands bound to the same decision and maps full snapshot`() {
        val clock = ManualClock()
        ServerTableHost(config(), clock = clock, controllers = List(4) { SeatController.RemoteHuman(false) },
            initialState = TestTableBuilder.create().also { it.cheatPoints.fill(40) }).use { host ->
            val initial = host.snapshot(0).await()
            val ids = (initial.view.myClosedTiles + listOfNotNull(initial.view.myDrawnTile)).map { it.tileId }
            val request = TileMarkRequest("first", initial.sessionId, initial.roundId!!, initial.decision.decisionId,
                initial.decision.controlEpoch, ids.take(4))
            val first = host.submitTileMark(0, request)
            val second = host.submitTileMark(0, request.copy(requestId = "second", tileIds = ids.drop(4).take(4)))
            assertEquals(ReceiptStatus.APPLIED, first.await().status)
            assertEquals(ReceiptStatus.REJECTED, second.await().status)
            val snapshot = host.snapshot(0).await()
            assertEquals(30, snapshot.cheatPoints[0])
            assertEquals(1, snapshot.tileMark!!.activeGroups.size)
            val protocol = EngineProtocolMapper.toProtocolSnapshot(snapshot)
            val decoded = ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = protocol))).snapshot!!
            assertEquals(protocol.tileMark, decoded.tileMark)
            assertEquals(EngineProtocolMapper.toEngineConfig(EngineProtocolMapper.toProtocolConfig(config())).tileMarkConfig, config().tileMarkConfig)
            assertEquals(first.await(), host.submitTileMark(0, request).await())
            for (seat in 1..3) assertTrue(host.snapshot(seat).await().tileMark!!.activeGroups.isEmpty())
            clock.now = initial.clocks.first { it.seat == 0 }.deadline + 1L
            assertEquals(first.await(), host.submitTileMark(0, request).await(), "A late retry retains the original receipt without charging")
            val late = host.submitTileMark(0, request.copy(requestId = "late-new",
                decisionId = snapshot.decision.decisionId, tileIds = ids.drop(4).take(4))).await()
            assertEquals("DEADLINE_EXPIRED", late.reason)
            assertEquals(30, host.snapshot(0).await().cheatPoints[0])
        }
    }

    @Test
    fun `binding validator and deadline are checked when queued marking actually executes`() {
        val clock = ManualClock()
        val serial = Executors.newSingleThreadExecutor()
        val release = CountDownLatch(1)
        val host = ServerTableHost(config(), clock = clock, controllers = List(4) { SeatController.RemoteHuman(false) },
            initialState = TestTableBuilder.create().also { it.cheatPoints.fill(40) }, serialExecutor = serial)
        try {
            val initial = host.snapshot(0).await()
            val req = TileMarkRequest("stale-binding", initial.sessionId, initial.roundId!!,
                initial.decision.decisionId, initial.decision.controlEpoch,
                (initial.view.myClosedTiles + listOfNotNull(initial.view.myDrawnTile)).take(4).map { it.tileId })
            val entered = CountDownLatch(1)
            serial.execute { entered.countDown(); check(release.await(5, TimeUnit.SECONDS)) }
            assertTrue(entered.await(3, TimeUnit.SECONDS))
            val valid = AtomicBoolean(true)
            val stale = host.submitTileMark(0, req) { valid.get() }
            assertFalse(stale.isDone)
            valid.set(false)
            clock.now = initial.clocks.first { it.seat == 0 }.deadline
            val expired = host.submitTileMark(0, req.copy(requestId = "expired"))
            release.countDown()
            assertEquals("STALE_SESSION", stale.await().reason)
            assertEquals("DEADLINE_EXPIRED", expired.await().reason)
            val after = host.snapshot(0).await()
            assertEquals(40, after.cheatPoints[0])
            assertTrue(after.tileMark!!.activeGroups.isEmpty())
        } finally {
            release.countDown()
            host.close()
            serial.shutdownNow()
        }
    }
}
