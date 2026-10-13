package com.majiang.presentation.game

import com.majiang.model.*
import com.majiang.client.api.FuritenStatusView
import com.majiang.client.api.LegalActionView
import com.majiang.client.api.PlayerPresentationPreviewView
import com.majiang.client.api.RiichiPreviewOptionView
import com.majiang.client.api.TileView
import com.majiang.presentation.game.RiichiSelectionModel
import com.majiang.session.*
import com.majiang.state.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class RiichiPreviewContractTest {
    private fun tileView(tile: Tile): TileView = TileView.fromTileId(tile.tileId)

    private fun preview(view: PlayerView): PlayerPresentationPreviewView {
        val options = RiichiPreviewProvider.options(view).map { option ->
            val discard = tileView(option.action.discardTile)
            RiichiPreviewOptionView(
                optionId = "riichi-${discard.tileId}",
                action = LegalActionView.DeclareRiichi(view.viewerSeat, discard),
                waits = option.waits.map(::tileView),
                furiten = FuritenStatusView()
            )
        }
        return PlayerPresentationPreviewView("round-1", "decision-1", 0L, riichiOptions = options)
    }
    private fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { val rank = it.digitToInt(); Tile(suit, rank, copies.getOrDefault(rank, 0).also { n -> copies[rank] = n + 1 }) }
    }
    private fun view(closed: List<Tile>, draw: Tile, choices: List<Tile>, melds: List<Meld> = emptyList()): PlayerView {
        val s = GameState().apply { phase = GamePhase.DISCARD; currentPlayer = 2 }
        s.players[2].closedTiles.addAll(closed)
        s.players[2].drawnTile = draw
        s.players[2].melds.addAll(melds)
        return PlayerView.fromGameState(s, 2, choices.map { GameAction.DeclareRiichi(2, it) })
    }
    private fun regular() = view(tiles(Suit.MAN, "123456") + tiles(Suit.PIN, "789") + tiles(Suit.SOU, "2345"),
        Tile(Suit.SOU, 6), listOf(Tile(Suit.SOU, 2), Tile(Suit.SOU, 6)))

    @Test fun `two discard choices expose different exact waits without mutation`() {
        val v = regular()
        val options = RiichiPreviewProvider.options(v)
        assertEquals(listOf(Tile(Suit.SOU, 2), Tile(Suit.SOU, 6)), options.map { it.action.discardTile })
        assertEquals(listOf(11, 14), options[0].waits.map { it.typeIndex })
        assertEquals(listOf(10, 13), options[1].waits.map { it.typeIndex })
        assertEquals(14, v.myAllClosedTiles.size)
        assertEquals(Tile(Suit.SOU, 6), v.myDrawnTile)
    }

    @Test fun `seven pairs and thirteen sided orphans show actual tile faces`() {
        val north = Tile(Suit.WIND, 4)
        val pairs = tiles(Suit.MAN, "1122") + tiles(Suit.PIN, "3344") + tiles(Suit.SOU, "5566") + Tile(Suit.WIND, 1)
        assertEquals(listOf(27), RiichiPreviewProvider.options(view(pairs, north, listOf(north))).single().waits.map { it.typeIndex })
        val orphans = tiles(Suit.MAN, "19") + tiles(Suit.PIN, "19") + tiles(Suit.SOU, "19") + tiles(Suit.WIND, "1234") + tiles(Suit.DRAGON, "123")
        val extra = Tile(Suit.MAN, 2)
        assertEquals(listOf(0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33),
            RiichiPreviewProvider.options(view(orphans, extra, listOf(extra))).single().waits.map { it.typeIndex })
    }

    @Test fun `existing kan excludes a fifth copy but keeps the other side of the wait`() {
        val kan = (0..3).map { Tile(Suit.MAN, 1, it) }
        val closed = tiles(Suit.MAN, "23") + tiles(Suit.PIN, "456") + tiles(Suit.SOU, "789") + tiles(Suit.WIND, "22")
        val discard = Tile(Suit.DRAGON, 3)
        val v = view(closed, discard, listOf(discard), listOf(Meld(MeldType.CLOSED_KAN, kan, -1, kan.last())))
        assertEquals(listOf(3), RiichiPreviewProvider.options(v).single().waits.map { it.typeIndex })
    }

    @Test fun `same kind physical discards remain distinct selectable actions`() {
        val discard0 = Tile(Suit.WIND, 1)
        val discard1 = Tile(Suit.WIND, 1, 1)
        val v = view(tiles(Suit.MAN, "123456") + tiles(Suit.PIN, "789") + tiles(Suit.SOU, "123") + discard0,
            discard1, listOf(discard0, discard1))
        val options = RiichiPreviewProvider.options(v)
        assertEquals(listOf(discard0, discard1), options.map { it.action.discardTile })
        assertTrue(options.all { it.waits.map { tile -> tile.typeIndex } == listOf(27) })
    }

    @Test fun `other seat forged discard wrong phase and already riichi yield no preview`() {
        val v = regular()
        for (bad in listOf(v.copy(currentTurnPlayer = 1), v.copy(phase = GamePhase.RESPONSE_WINDOW),
            v.copy(riichiStatus = listOf(false, false, true, false)),
            v.copy(legalActions = listOf(GameAction.DeclareRiichi(1, Tile(Suit.SOU, 2)))),
            v.copy(legalActions = listOf(GameAction.DeclareRiichi(2, Tile(Suit.SOU, 2, 3)))))) {
            assertTrue(RiichiPreviewProvider.options(bad).isEmpty())
        }
    }

    @Test fun `public discard counts do not hide theoretical waits and result lists are immutable`() {
        val v = regular()
        val river = (0..3).map { Tile(Suit.SOU, 2, it) } + (0..3).map { Tile(Suit.SOU, 5, it) }
        val options = RiichiPreviewProvider.options(v.copy(discards = listOf(river, emptyList(), emptyList(), emptyList())))
        assertEquals(listOf(10, 13), options.last().waits.map { it.typeIndex })
        assertThrows(UnsupportedOperationException::class.java) { (options as MutableList).clear() }
        assertThrows(UnsupportedOperationException::class.java) { (options.last().waits as MutableList).clear() }
    }

    @Test fun `selection needs deliberate entry and explicit tile selection`() {
        val model = RiichiSelectionModel()
        model.update(preview(regular()))
        assertFalse(model.isSelecting)
        assertNull(model.confirmationAction())
        assertFalse(model.select(tileView(Tile(Suit.SOU, 2))))
        assertTrue(model.begin())
        assertNull(model.selectedTile)
        assertNull(model.confirmationAction())
        assertTrue(model.select(tileView(Tile(Suit.SOU, 6))))
        assertEquals(LegalActionView.DeclareRiichi(2, tileView(Tile(Suit.SOU, 6))), model.confirmationAction())
        assertTrue(model.begin())
        assertEquals(tileView(Tile(Suit.SOU, 6)), model.selectedTile)
        assertFalse(model.select(tileView(Tile(Suit.MAN, 9))))
        assertEquals(tileView(Tile(Suit.SOU, 6)), model.selectedTile)
        assertTrue(model.select(tileView(Tile(Suit.SOU, 2))))
        assertEquals(LegalActionView.DeclareRiichi(2, tileView(Tile(Suit.SOU, 2))), model.confirmationAction())
    }

    @Test fun `hover query never changes selection and cancellation clears it`() {
        val model = RiichiSelectionModel()
        model.update(preview(regular())); model.begin(); model.select(tileView(Tile(Suit.SOU, 2)))
        assertEquals(listOf(10, 13), model.previewFor(tileView(Tile(Suit.SOU, 6)))!!.waits.map { it.typeIndex })
        assertEquals(tileView(Tile(Suit.SOU, 2)), model.selectedTile)
        model.cancel()
        assertFalse(model.isSelecting)
        assertNull(model.confirmationAction())
        assertNull(model.selectedTile)
    }

    @Test fun `equal snapshot preserves choice but new view invalidates it`() {
        val v = regular()
        val model = RiichiSelectionModel()
        model.update(preview(v)); model.begin(); model.select(tileView(Tile(Suit.SOU, 6)))
        model.update(preview(v.copy()))
        assertTrue(model.isSelecting)
        assertEquals(tileView(Tile(Suit.SOU, 6)), model.selectedTile)
        model.update(preview(v.copy(legalActions = emptyList())))
        assertFalse(model.isSelecting)
        assertNull(model.confirmationAction())
        assertFalse(model.begin())
    }
}
