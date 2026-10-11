package com.majiang.screen

import com.majiang.client.api.*
import com.majiang.presentation.game.TileMarkSelectionModel
import com.majiang.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class TileMarkSelectionModelContractTest {
    private fun snapshot(): GameSnapshotView {
        val tiles = (0..13).map { TestViewBuilder.tile(it) }
        val decision = DecisionView("round", "decision", 3, emptyList())
        return GameSnapshotView("session", "round", "epoch", 1, 1, 1,
            TestViewBuilder.playerView(viewerSeat = 0, myClosedTiles = tiles.take(13), myDrawnTile = tiles.last()),
            decision, clocks = listOf(ClockInfo(0, 1000, 24000, 20000, 1000)),
            cheatPoints = List(4) { 40 }, controls = listOf(ControlsInfo(0, true, "HUMAN", 3)),
            tileMark = TileMarkView(true, null, 10, 0, 12, tiles.map { it.tileId }, "decision", 3, 1, emptyList()))
    }

    @Test
    fun `selects four physical copies including drawn tile without permitting fifth or foreign tile`() {
        val model = TileMarkSelectionModel { 100L }
        val snap = snapshot()
        model.update(snap)
        assertTrue(model.begin())
        for (id in listOf(0, 1, 2)) assertTrue(model.toggle(id))
        assertFalse(model.canConfirm)
        assertNull(model.buildRequest("three"))
        assertTrue(model.toggle(13))
        assertFalse(model.toggle(4))
        assertFalse(model.toggle(100))
        assertTrue(model.canConfirm)
        val req = model.buildRequest("cast")!!
        assertEquals(setOf(0, 1, 2, 13), req.tileIds.toSet())
        assertEquals("session", req.sessionId)
        assertEquals("round", req.roundId)
        assertEquals("decision", req.decisionId)
        assertEquals(3L, req.controlEpoch)
        assertEquals(40, snap.cheatPoints[0])
        assertTrue(snap.tileMark!!.activeGroups.isEmpty())
        assertTrue(model.toggle(1))
        assertFalse(model.canConfirm)
        model.cancel()
        assertFalse(model.isSelecting)
        assertTrue(model.selectedTileIds.isEmpty())
    }

    @Test
    fun `sorting retains physical selection but decision identity or candidate set invalidates it`() {
        val base = snapshot()
        val model = TileMarkSelectionModel { 100L }
        model.update(base)
        model.begin()
        listOf(0, 1, 2, 13).forEach { model.toggle(it) }
        model.update(base.copy(view = base.view.copy(myClosedTiles = base.view.myClosedTiles.reversed()),
            tileMark = base.tileMark!!.copy(selectableTileIds = base.tileMark!!.selectableTileIds.reversed())))
        assertEquals(setOf(0, 1, 2, 13), model.selectedTileIds.toSet())
        val alternatives = listOf(
            base.copy(sessionId = "new-session"), base.copy(syncEpoch = "new-epoch"),
            base.copy(roundId = "new-round", decision = base.decision.copy(roundId = "new-round")),
            base.copy(decision = base.decision.copy(decisionId = "next"), tileMark = base.tileMark!!.copy(decisionId = "next")),
            base.copy(decision = base.decision.copy(controlEpoch = 4), tileMark = base.tileMark!!.copy(controlEpoch = 4)),
            base.copy(tileMark = base.tileMark!!.copy(selectableTileIds = (1..13).toList())))
        for (changed in alternatives) {
            model.update(base)
            assertTrue(model.begin())
            listOf(0, 1, 2, 13).forEach { model.toggle(it) }
            model.update(changed)
            assertFalse(model.isSelecting)
            assertTrue(model.selectedTileIds.isEmpty())
            assertNull(model.buildRequest("obsolete"))
        }
    }

    @Test
    fun `authority deadline elapsed locally prevents confirmation without requiring a fresh push`() {
        var localTime = 100_000L
        val model = TileMarkSelectionModel { localTime }
        val received = snapshot()
        model.update(received)
        model.begin()
        (0..3).forEach { model.toggle(it) }
        localTime += 10_000
        model.update(received)
        localTime += 12_999
        assertTrue(model.canConfirm)
        localTime += 1
        assertFalse(model.canConfirm)
        assertNull(model.buildRequest("late"))
        assertFalse(model.toggle(0))
    }

    @Test
    fun `new sync epoch replaces expired clock sample even when cursor and decision are unchanged`() {
        var localTime = 100_000L
        val model = TileMarkSelectionModel { localTime }
        val old = snapshot()
        model.update(old)
        assertTrue(model.begin())
        (0..3).forEach { model.toggle(it) }
        localTime += 23_000
        assertFalse(model.canConfirm)
        model.update(old.copy(syncEpoch = "replacement-stream",
            clocks = listOf(ClockInfo(0, 24_000, 47_000, 20_000, 24_000))))
        assertFalse(model.isSelecting)
        assertTrue(model.selectedTileIds.isEmpty())
        assertTrue(model.begin(), "A replacement stream must use its own authoritative clock sample")
        (0..3).forEach { model.toggle(it) }
        assertTrue(model.canConfirm)
    }

    @Test
    fun `pause disconnect pending takeover disabled feature and insufficient quota block stale drafts`() {
        val base = snapshot()
        val model = TileMarkSelectionModel { 100L }
        fun select() {
            model.update(base)
            assertTrue(model.begin())
            (0..3).forEach { model.toggle(it) }
            assertTrue(model.canConfirm)
        }
        for (invalid in listOf(base.copy(paused = true),
            base.copy(controls = listOf(ControlsInfo(0, true, "TAKEOVER", 3))),
            base.copy(tileMark = null),
            base.copy(tileMark = base.tileMark!!.copy(canApply = false, disabledReason = "TILE_MARK_CAPACITY_EXCEEDED")),
            base.copy(cheatPoints = listOf(9, 40, 40, 40)), base.copy(clocks = emptyList()))) {
            select()
            model.update(invalid)
            assertFalse(model.canConfirm)
            assertNull(model.buildRequest("blocked"))
            model.update(base)
            assertFalse(model.isSelecting, "Restoring availability must not restore an invalidated draft")
            assertTrue(model.selectedTileIds.isEmpty())
        }
        select()
        model.update(base, connected = false)
        assertNull(model.buildRequest("disconnected"))
        model.update(base)
        assertTrue(model.selectedTileIds.isEmpty())
        select()
        model.update(base, pending = true)
        assertNull(model.buildRequest("in-flight"))
        model.update(base, pending = false)
        assertFalse(model.isSelecting, "Completion of another pending action must not restore a stale draft")
        assertTrue(model.selectedTileIds.isEmpty())
        model.update(null)
        assertFalse(model.isSelecting)
        assertTrue(model.selectedTileIds.isEmpty())
    }
}
