package com.majiang.presentation.game

import com.majiang.client.api.GameEventView
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import com.majiang.presentation.common.render.DiscardAnimation
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class DiscardAnimationControllerContractTest {

    private fun controller(initial: List<List<TileView>> = List(4) { emptyList() }) =
        DiscardAnimationController(
            startPositionProvider = { seat -> TableBoardPositions.start(seat) },
            slotPositionProvider = { seat, index -> TableBoardPositions.slot(seat, index) },
            initialDiscards = initial
        )

    @Test
    fun `batched discards follow authoritative event order and never enqueue twice`() {
        val first = TestViewBuilder.tile(SuitView.MAN, 1, 0)
        val second = TestViewBuilder.tile(SuitView.PIN, 2, 0)
        val discards = listOf(
            listOf(first),
            listOf(second),
            emptyList(),
            emptyList()
        )
        val events = listOf(
            GameEventView.TileDiscarded(1, 0, first),
            GameEventView.TileDiscarded(2, 1, second)
        )

        val c = controller()
        assertTrue(c.checkDiscardsDiff(discards, events))
        assertEquals(listOf(0, 1), c.discardQueue.map { it.seat })

        // 同一批事件重放时，基准牌河已经包含两张牌，不得重复入队。
        c.checkDiscardsDiff(discards, events)
        assertEquals(2, c.discardQueue.size, "Replayed events must not duplicate animation actions")

        c.fastForward(discards)
        assertFalse(c.isDiscardPlaybackBusy)
        assertEquals(discards, c.lastDiscards)
    }

    @Test
    fun `a removed river tile clears its in-flight and queued animation`() {
        val tile = TestViewBuilder.tile(SuitView.SOU, 5, 0)
        val withTile: List<List<TileView>> = listOf(listOf(tile), emptyList(), emptyList(), emptyList())
        val c = controller()

        c.checkDiscardsDiff(withTile, listOf(GameEventView.TileDiscarded(1, 0, tile)))
        c.update(0.05f)
        assertEquals(tile, c.currentDiscardAnim?.tile)

        val removed: List<List<TileView>> = List(4) { emptyList() }
        c.checkDiscardsDiff(removed, emptyList())
        assertNull(c.currentDiscardAnim)
        assertFalse(c.isDiscardPlaybackBusy)
    }

    @Test
    fun `animation clock semantics are preserved`() {
        val tile = TestViewBuilder.tile(SuitView.MAN, 9, 0)
        val discards = listOf(listOf(tile), emptyList(), emptyList(), emptyList())
        val c = controller()
        c.checkDiscardsDiff(discards, listOf(GameEventView.TileDiscarded(1, 0, tile)))
        c.update(0.01f)
        assertNotNull(c.currentDiscardAnim)
        assertEquals(0.01f, c.discardAnimElapsed, 1e-5f)

        c.update(DiscardAnimation.TOTAL_DURATION)
        assertNull(c.currentDiscardAnim)
        assertEquals(0f, c.discardAnimElapsed, 1e-5f)
    }

    /** Stable headless positions matching the shared renderer geometry without touching GL. */
    private object TableBoardPositions {
        fun start(seat: Int): Pair<Float, Float> = when ((seat + 4) % 4) {
            0 -> 640f to 100f
            1 -> 1180f to 360f
            2 -> 640f to 620f
            else -> 100f to 360f
        }

        fun slot(seat: Int, index: Int): Pair<Float, Float> =
            (480f + (index % 6) * 40f) to (240f - (index / 6) * 56f)
    }
}
