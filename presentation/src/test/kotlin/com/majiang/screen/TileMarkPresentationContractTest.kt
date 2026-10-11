package com.majiang.screen

import com.majiang.client.api.*
import com.majiang.presentation.game.TileMarkPresentation
import com.majiang.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class TileMarkPresentationContractTest {
    private fun snapshot(tiles: List<MarkedTileView>, rivers: List<List<TileView>>): GameSnapshotView {
        val group = TileMarkGroupView("group", "round", 1, 4, 3, false, tiles)
        return GameSnapshotView("session", "round", "epoch", 1, 1, 1,
            TestViewBuilder.playerView(viewerSeat = 0, discards = rivers), DecisionView("round", "decision", 0),
            tileMark = TileMarkView(false, "NOT_YOUR_TURN", 10, 4, 12, emptyList(), "decision", 0, 1, listOf(group)))
    }

    @Test
    fun `four seats and row boundaries locate physical copies and describe one based positions`() {
        val indexes = listOf(0, 5, 6, 12)
        val tiles = (0..3).map { seat -> MarkedTileView(seat, TestViewBuilder.tile(seat), TileMarkArea.DISCARD,
            seat, RiverPositionView("round", seat, indexes[seat])) }
        val rivers = (0..3).map { seat -> List(indexes[seat]) { TestViewBuilder.tile(40 + seat * 16 + it) } + TestViewBuilder.tile(seat) }
        val snapshot = snapshot(tiles, rivers)
        assertEquals(tiles, TileMarkPresentation.riverMarks(snapshot))
        for (viewer in 0..3) for (seat in 0..3) {
            val text = TileMarkPresentation.locationText(tiles[seat], viewer)
            assertTrue(text.contains("第 ${indexes[seat] + 1} 张"), text)
            val relative = listOf("自己", "下家", "对家", "上家")[(seat - viewer + 4) % 4]
            assertTrue(text.contains(relative), text)
        }
        assertTrue(TileMarkPresentation.riverMarks(snapshot.copy(tileMark = null)).isEmpty())
    }

    @Test
    fun `opponent recognition is current seat authorized faces only and groups retain independent duration`() {
        val tiles = listOf(
            MarkedTileView(0, TestViewBuilder.tile(0), TileMarkArea.OPPONENT_HAND, 1),
            MarkedTileView(1, TestViewBuilder.tile(1), TileMarkArea.OPPONENT_HAND, 2),
            MarkedTileView(2, TestViewBuilder.tile(2), TileMarkArea.OWN_HAND, 0),
            MarkedTileView(3, TestViewBuilder.tile(3), TileMarkArea.NOT_VISIBLE))
        val snapshot = snapshot(tiles, List(4) { emptyList() })
        assertEquals(listOf(tiles[0]), TileMarkPresentation.opponentMarks(snapshot, 1))
        assertEquals(listOf(tiles[1]), TileMarkPresentation.opponentMarks(snapshot, 2))
        assertTrue(TileMarkPresentation.opponentMarks(snapshot, 0).isEmpty())
        assertTrue(TileMarkPresentation.opponentMarks(snapshot, 3).isEmpty())
        assertTrue(TileMarkPresentation.riverMarks(snapshot).isEmpty())
        assertEquals("当前不可见", TileMarkPresentation.locationText(tiles.last(), 0))
        val group = snapshot.tileMark!!.activeGroups.single()
        assertEquals("另余 3 局", TileMarkPresentation.durationText(group))
        assertEquals("另余 1 局", TileMarkPresentation.durationText(group.copy(futureRoundsRemaining = 1)))
        assertEquals("本局最后有效", TileMarkPresentation.durationText(group.copy(futureRoundsRemaining = 0, isLastEffectiveRound = true)))
        assertTrue(TileMarkPresentation.opponentMarks(snapshot.copy(tileMark = null), 1).isEmpty())
    }
}
