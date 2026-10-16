package com.majiang.client.api

import com.majiang.client.mapping.ProtocolViewMapper
import com.majiang.client.session.ServerConnectionSession
import com.majiang.server.api.RoomBinding
import com.majiang.transport.api.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.io.IOException
import java.util.concurrent.CompletableFuture

/** Codex-owned tests for private mark mapping, ordering and asynchronous submission. */
class TileMarkClientContractTest {
    private fun request(id: String = "mark") = TileMarkRequest(
        id, "session-1", "round-1", "dec-cursor-10", 1L, listOf(20, 21, 22, 23))

    private fun snapshot(fake: ServerConnectionSessionTest.FakeServerConnection, cursor: Long,
                         groups: Int = 1): ProtocolPlayerSnapshot {
        val base = fake.createSnapshot(cursor)
        val river = listOf(ProtocolTile.fromTileId(40), ProtocolTile.fromTileId(20))
        val marks = (0 until groups).map { group ->
            ProtocolTileMarkGroupView("group-$group", "round-1", 1, 4, 3, false,
                (20 + group * 4 until 24 + group * 4).map { id ->
                    if (id == 20) ProtocolMarkedTileView(id, ProtocolTile.fromTileId(id),
                        ProtocolTileMarkArea.DISCARD, 2, ProtocolRiverPosition("round-1", 2, 1))
                    else ProtocolMarkedTileView(id, ProtocolTile.fromTileId(id), ProtocolTileMarkArea.NOT_VISIBLE)
                })
        }
        return base.copy(view = base.view.copy(discards = listOf(emptyList(), emptyList(), river, emptyList())),
            tileMark = ProtocolTileMarkView(false, "NOT_YOUR_TURN", 10, groups * 4, 12, emptyList(),
                base.decision.decisionId, 1, cursor, marks))
    }

    @Test
    fun `mapping retains private river position config and immutable collections`() {
        val fake = ServerConnectionSessionTest.FakeServerConnection()
        val mapped = ProtocolViewMapper.mapSnapshot(snapshot(fake, 10, 3))
        val mark = mapped.tileMark!!
        assertEquals(12, mark.activeTileCount)
        assertEquals(3, mark.activeGroups.size)
        assertEquals(10, mark.cost)
        val first = mark.activeGroups.first().tiles.first()
        assertEquals(TileMarkArea.DISCARD, first.area)
        assertEquals(RiverPositionView("round-1", 2, 1), first.riverPosition)
        assertEquals(20, first.tile.tileId)
        assertEquals(mark, mapped.copy().tileMark)
        assertEquals(mapped, mapped.deepCopy())
        assertThrows(UnsupportedOperationException::class.java) { (mark.activeGroups as MutableList).clear() }
        assertThrows(UnsupportedOperationException::class.java) { (mark.activeGroups.first().tiles as MutableList).clear() }
        val source = mutableListOf(20, 21, 22, 23)
        val req = request().copy(tileIds = source)
        source.clear()
        assertEquals(listOf(20, 21, 22, 23), req.tileIds)
        assertThrows(UnsupportedOperationException::class.java) { (req.tileIds as MutableList).clear() }
        assertEquals(ProtocolTileMarkRequest("mark", "session-1", "round-1", "dec-cursor-10", 1, req.tileIds),
            ProtocolViewMapper.toProtocolTileMarkRequest(req))
        val config = ProtocolMatchConfig(cheatEconomy = ProtocolCheatEconomyConfig(
            true, List(4) { 40 }, mapOf("TILE_MARK" to 10)), tileMarkConfig = ProtocolTileMarkConfig(true))
        assertEquals(config, ProtocolViewMapper.toProtocolMatchConfig(ProtocolViewMapper.mapMatchConfig(config).copy()))
        assertTrue(ProtocolViewMapper.mapCapabilities(ProtocolAccessCapabilities(tileMarkSupported = true)).tileMarkSupported)
        assertFalse(ProtocolViewMapper.mapCapabilities(ProtocolAccessCapabilities()).tileMarkSupported)
        assertNull(ProtocolViewMapper.mapSnapshot(fake.defaultSnapshot).tileMark)
    }

    @Test
    fun `mark shares pending lock with discard and shuffle and does not update cache optimistically`() {
        val fake = ServerConnectionSessionTest.FakeServerConnection()
        val delayed = CompletableFuture<ProtocolActionReceipt>()
        fake.customSubmitMarkFuture = delayed
        ServerConnectionSession(fake).use { session ->
            session.createRoom("Host", null).join()
            val before = session.currentSnapshot
            val result = session.submitTileMark(request())
            assertTrue(session.isPending)
            assertFalse(result.isDone)
            assertEquals(ProtocolViewMapper.toProtocolTileMarkRequest(request()), fake.lastSubmittedMark)
            assertEquals(before, session.currentSnapshot)
            assertEquals("ACTION_IN_FLIGHT", session.submitTileMark(request("second")).join().reason)
            assertEquals("ACTION_IN_FLIGHT", session.submit(ActionChoice("discard", "round-1", "dec-cursor-10", 1, "opt-1")).join().reason)
            assertEquals("ACTION_IN_FLIGHT", session.submitShufflePlan(ShufflePlan("shuffle", "round-1", 1, ShuffleSelection())).join().reason)
            delayed.complete(ProtocolActionReceipt("mark", ProtocolReceiptStatus.REJECTED, reason = "INSUFFICIENT_CHEAT_POINTS"))
            assertEquals("INSUFFICIENT_CHEAT_POINTS", result.join().reason)
            assertFalse(session.isPending)
            assertEquals(before, session.currentSnapshot)
        }
    }

    @Test
    fun `push before receipt stays current and full snapshots remove expired groups and null effects`() {
        val fake = ServerConnectionSessionTest.FakeServerConnection()
        val delayed = CompletableFuture<ProtocolActionReceipt>()
        fake.customSubmitMarkFuture = delayed
        ServerConnectionSession(fake).use { session ->
            session.createRoom("Host", null).join()
            val pending = session.submitTileMark(request())
            fake.emitUpdate(ProtocolPlayerUpdate(snapshot(fake, 11, 2), 10, emptyList()))
            assertEquals(8, session.currentSnapshot!!.tileMark!!.activeTileCount)
            delayed.complete(ProtocolActionReceipt("mark", ProtocolReceiptStatus.APPLIED, cursor = 11))
            assertEquals(ReceiptStatus.APPLIED, pending.join().status)
            fake.emitUpdate(ProtocolPlayerUpdate(snapshot(fake, 12, 1), 11, emptyList()))
            assertEquals(listOf("group-0"), session.currentSnapshot!!.tileMark!!.activeGroups.map { it.groupId })
            fake.emitUpdate(ProtocolPlayerUpdate(snapshot(fake, 11, 2), 10, emptyList()))
            assertEquals(12L, session.currentSnapshot!!.cursor)
            assertEquals(4, session.currentSnapshot!!.tileMark!!.activeTileCount)
            fake.emitUpdate(ProtocolPlayerUpdate(snapshot(fake, 13, 0), 12, emptyList()))
            assertTrue(session.currentSnapshot!!.tileMark!!.activeGroups.isEmpty())
            fake.emitUpdate(ProtocolPlayerUpdate(fake.createSnapshot(14), 13, emptyList()))
            assertNull(session.currentSnapshot!!.tileMark)
        }
    }

    @Test
    fun `out of order updates and explicit resume replace mark state without resurrecting groups`() {
        val fake = ServerConnectionSessionTest.FakeServerConnection()
        ServerConnectionSession(fake).use { session ->
            session.createRoom("Host", null).join()
            fake.customResumeCursorFuture = CompletableFuture()
            fake.emitUpdate(ProtocolPlayerUpdate(snapshot(fake, 12, 0), 11, emptyList()))
            fake.emitUpdate(ProtocolPlayerUpdate(snapshot(fake, 11, 1), 10, emptyList()))
            assertEquals(12L, session.currentSnapshot!!.cursor)
            assertEquals(0, session.currentSnapshot!!.tileMark!!.activeTileCount)
            fake.customResumeCursorFuture!!.complete(snapshot(fake, 12, 0))
            fake.customResumeCursorFuture = CompletableFuture.completedFuture(snapshot(fake, 20, 3))
            assertEquals(12, session.resume(0).join().tileMark!!.activeTileCount)
            fake.customSnapshotFuture = CompletableFuture.completedFuture(fake.createSnapshot(21))
            assertNull(session.snapshot().join().tileMark)
            assertNull(session.currentSnapshot!!.tileMark)
        }
    }

    @Test
    fun `sync failure async failure cancellation and close release only their own pending action`() {
        val fake = ServerConnectionSessionTest.FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()
        fake.synchronousFailure = "submitTileMark"
        assertTrue(session.submitTileMark(request()).isCompletedExceptionally)
        assertFalse(session.isPending)
        fake.synchronousFailure = null
        fake.customSubmitMarkFuture = CompletableFuture.failedFuture(IOException("network"))
        assertTrue(session.submitTileMark(request()).isCompletedExceptionally)
        assertFalse(session.isPending)
        val old = CompletableFuture<ProtocolActionReceipt>()
        fake.customSubmitMarkFuture = old
        assertTrue(session.submitTileMark(request()).cancel(false))
        assertFalse(session.isPending)
        val fresh = CompletableFuture<ProtocolActionReceipt>()
        fake.customSubmitMarkFuture = fresh
        val pending = session.submitTileMark(request("fresh"))
        old.complete(ProtocolActionReceipt("mark", ProtocolReceiptStatus.APPLIED, cursor = 11))
        assertTrue(session.isPending)
        assertFalse(pending.isDone)
        session.close()
        assertTrue(pending.isCompletedExceptionally)
        assertFalse(session.isPending)
        fresh.complete(ProtocolActionReceipt("fresh", ProtocolReceiptStatus.APPLIED, cursor = 12))
        assertFalse(session.isPending)
        assertTrue(session.submitTileMark(request()).isCompletedExceptionally)
    }

    @Test
    fun `room replacement retires old request and spectator never retains private marks`() {
        val fake = ServerConnectionSessionTest.FakeServerConnection()
        val old = CompletableFuture<ProtocolActionReceipt>()
        fake.customSubmitMarkFuture = old
        ServerConnectionSession(fake).use { session ->
            session.createRoom("Host", null).join()
            fake.emitUpdate(ProtocolPlayerUpdate(snapshot(fake, 11), 10, emptyList()))
            val retired = session.submitTileMark(request())
            session.leaveRoom().join()
            assertTrue(retired.isCompletedExceptionally)
            assertNull(session.currentSnapshot)
            fake.customCreateRoomFuture = CompletableFuture.completedFuture(RoomBinding("NEW", 0, "new-token", fake.defaultSnapshot))
            session.createRoom("New Host", null).join()
            val fresh = CompletableFuture<ProtocolActionReceipt>()
            fake.customSubmitMarkFuture = fresh
            val next = session.submitTileMark(request("new-mark"))
            old.complete(ProtocolActionReceipt("mark", ProtocolReceiptStatus.APPLIED, cursor = 100))
            assertTrue(session.isPending)
            assertFalse(next.isDone)
            assertNull(session.currentSnapshot!!.tileMark)
            fresh.complete(ProtocolActionReceipt("new-mark", ProtocolReceiptStatus.REJECTED, reason = "TILE_MARK_DISABLED"))
            next.join()
            session.leaveRoom().join()
            session.joinSpectator("ROOM1234", "Watcher").join()
            assertNull(session.currentSnapshot)
            val previouslySent = fake.lastSubmittedMark
            assertTrue(session.submitTileMark(request()).isCompletedExceptionally)
            assertEquals(previouslySent, fake.lastSubmittedMark)
        }
    }

    @Test
    fun `automatic reconnect restores all groups and old connection completion cannot unlock the new action`() {
        val clock = ServerConnectionSessionTest.FakeTransportClock()
        val scheduler = ServerConnectionSessionTest.FakeTransportScheduler(clock)
        val first = ServerConnectionSessionTest.FakeServerConnection()
        val second = ServerConnectionSessionTest.FakeServerConnection()
        val restored = snapshot(second, 50, 3)
        second.customResumeSessionFuture = CompletableFuture.completedFuture(RoomBinding("ROOM1234", 0, "token-host", restored))
        second.customSnapshotFuture = CompletableFuture.completedFuture(restored)
        second.customResumeCursorFuture = CompletableFuture.completedFuture(restored)
        val oldWire = CompletableFuture<ProtocolActionReceipt>()
        first.customSubmitMarkFuture = oldWire
        ServerConnectionSession(first, connectionFactory = { CompletableFuture.completedFuture(second) },
            clock = clock, scheduler = scheduler).use { session ->
            session.createRoom("Host", null).join()
            val retired = session.submitTileMark(request())
            first.emitDisconnect("NETWORK_DROP")
            assertEquals(ConnectionState.RECONNECTING, session.connectionState)
            scheduler.advanceTime(500L)
            assertEquals(ConnectionState.CONNECTED, session.connectionState)
            assertTrue(retired.isCompletedExceptionally)
            assertEquals(12, session.currentSnapshot!!.tileMark!!.activeTileCount)
            assertEquals(50L, session.currentSnapshot!!.cursor)
            val newWire = CompletableFuture<ProtocolActionReceipt>()
            second.customSubmitMarkFuture = newWire
            val pending = session.submitTileMark(request("new-connection"))
            oldWire.complete(ProtocolActionReceipt("mark", ProtocolReceiptStatus.APPLIED, cursor = 11))
            assertTrue(session.isPending)
            assertFalse(pending.isDone)
            assertEquals(12, session.currentSnapshot!!.tileMark!!.activeTileCount)
            assertEquals(50L, session.currentSnapshot!!.cursor)
            newWire.complete(ProtocolActionReceipt("new-connection", ProtocolReceiptStatus.REJECTED, reason = "TILE_MARK_LIMIT_REACHED"))
            assertEquals("TILE_MARK_LIMIT_REACHED", pending.join().reason)
            assertFalse(session.isPending)
        }
    }
}
