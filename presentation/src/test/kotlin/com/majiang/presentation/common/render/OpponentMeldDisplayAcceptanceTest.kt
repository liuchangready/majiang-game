package com.majiang.presentation.common.render

import com.badlogic.gdx.math.Rectangle
import com.majiang.client.api.GamePhaseView
import com.majiang.client.api.MeldTypeView
import com.majiang.client.api.MeldView
import com.majiang.client.api.PlayerView
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class OpponentMeldDisplayAcceptanceTest {

    private fun tile(suit: SuitView, rank: Int, copyIndex: Int = 0): TileView {
        val base = when (suit) {
            SuitView.MAN -> (rank - 1) * 4
            SuitView.SOU -> 36 + (rank - 1) * 4
            SuitView.PIN -> 72 + (rank - 1) * 4
            SuitView.WIND -> 108 + (rank - 1) * 4
            SuitView.DRAGON -> 124 + (rank - 1) * 4
        }
        return TileView(base + copyIndex, suit, rank)
    }

    private fun meld(type: MeldTypeView, seat: Int, rank: Int = seat + 1): MeldView {
        val tiles = if (type == MeldTypeView.CHI) {
            (2..4).map { tile(SuitView.MAN, it) }
        } else {
            (0 until if (type == MeldTypeView.PON) 3 else 4).map { tile(SuitView.PIN, rank, it) }
        }
        return MeldView(type, tiles, if (type == MeldTypeView.CLOSED_KAN) -1 else (seat + 3) % 4, tiles.first())
    }

    private fun view(viewer: Int, melds: List<List<MeldView>>): PlayerView =
        PlayerView(
            viewerSeat = viewer,
            phase = GamePhaseView.DISCARD,
            roundWind = 1,
            roundNumber = 0,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = viewer,
            wallRemaining = 70,
            doraIndicators = emptyList(),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = List(4) { emptyList() },
            melds = melds,
            handTileCounts = listOf(13, 13, 13, 13),
            myClosedTiles = (1..13).map { tile(SuitView.MAN, it % 9 + 1) },
            myDrawnTile = null,
            legalActions = emptyList()
        )

    private fun rect(t: MeldDisplayTile) = Rectangle(t.x, t.y, t.width, t.height)

    @Test
    fun `all public melds follow viewer relative seats and preserve physical tile identity`() {
        val source = List(4) { seat -> listOf(meld(MeldTypeView.entries[seat], seat)) }
        for (viewer in 0..3) {
            val v = view(viewer, source)
            val groups = MeldDisplayLayout.layoutAll(v)
            assertEquals(4, groups.size)
            assertEquals((0..3).toSet(), groups.map { it.ownerSeat }.toSet())
            for (g in groups) {
                assertEquals(source[g.ownerSeat].single().tiles, g.tiles.map { it.tile })
                when ((g.ownerSeat - viewer + 4) % 4) {
                    0 -> assertTrue(g.tiles.all { it.y < 145 && it.x >= 220 })
                    1 -> assertTrue(g.tiles.all { it.x >= 950 && it.y >= 200 })
                    2 -> assertTrue(g.tiles.all { it.y >= 600 && it.y + it.height <= 655 })
                    3 -> assertTrue(g.tiles.all { it.x + it.width < 340 && it.y >= 200 })
                }
            }
            assertEquals(MeldDisplayLayout.layout(v), groups.filter { it.ownerSeat == viewer })
        }
    }

    @Test
    fun `maximum display capacity stays clear of other tiles hand controls and HUD`() {
        // Layout capacity fixture: 4 kans per seat = 16 kans total
        val source = List(4) { seat -> List(4) { meld(MeldTypeView.CLOSED_KAN, seat, it + 1) } }
        for (viewer in 0..3) {
            val v = view(viewer, source)
            val groups = MeldDisplayLayout.layoutAll(v)
            assertEquals(16, groups.size)
            val tiles = groups.flatMap { it.tiles }
            for (tile in tiles) {
                assertTrue(tile.x >= 0 && tile.x + tile.width <= 1280)
                assertTrue(tile.y >= 25 && tile.y + tile.height <= 655)
                // 不遮挡手牌中央操作区 (230, 145, 970, 44)
                assertFalse(rect(tile).overlaps(Rectangle(230f, 145f, 970f, 44f)))
            }
            for (i in tiles.indices) for (j in i + 1 until tiles.size) {
                assertFalse(rect(tiles[i]).overlaps(rect(tiles[j])), "Tiles overlap: $i / $j, viewer $viewer")
            }
        }
    }
}
