package com.majiang.render

import com.majiang.presentation.common.render.*

import com.badlogic.gdx.math.Rectangle
import com.majiang.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.game.GameScreen
import com.majiang.testing.FakeGameSession
import com.majiang.testing.FakeTableNavigation
import com.majiang.testing.TestUiAssets
import com.majiang.testing.TestViewBuilder as T
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class OpponentMeldDisplayAcceptanceTest : GdxHeadlessTestRunner() {
    private fun meld(type: MeldTypeView, seat: Int, rank: Int = seat + 1): MeldView {
        val tiles = if (type == MeldTypeView.CHI) (2..4).map { T.tile(SuitView.MAN, it) }
            else (0 until if (type == MeldTypeView.PON) 3 else 4).map { T.tile(SuitView.PIN, rank, it) }
        return MeldView(type, tiles, if (type == MeldTypeView.CLOSED_KAN) -1 else (seat + 3) % 4, tiles.first())
    }
    private fun view(viewer: Int, melds: List<List<MeldView>>): PlayerView = T.playerView(viewerSeat = viewer, melds = melds)
    private fun rect(t: MeldDisplayTile) = Rectangle(t.x, t.y, t.width, t.height)
    private fun screen(session: FakeGameSession) = GameScreen(TestUiAssets.create(), session, FakeTableNavigation())

    @Test fun `all public melds follow viewer relative seats and preserve physical tile identity`() {
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
            val screen = screen(FakeGameSession(initialView = v))
            try {
                assertEquals(groups, MeldDisplayLayout.layoutAll(screen.currentView))
                assertEquals(Pair(750f, 440f), screen.getDiscardSlotPosition((viewer + 1) % 4, 0))
                assertEquals(Pair(460f, 530f), screen.getDiscardSlotPosition((viewer + 2) % 4, 0))
                assertTrue(screen.getDiscardSlotPosition((viewer + 2) % 4, 6).second < 530f)
            } finally { screen.dispose() }
        }
    }

    @Test fun `maximum display capacity stays clear of other tiles hand controls and HUD`() {
        val source = List(4) { seat -> List(4) { meld(MeldTypeView.CLOSED_KAN, seat, it + 1) } }
        for (viewer in 0..3) {
            val v = view(viewer, source).copy(myClosedTiles = listOf(T.tile(SuitView.WIND, 1)), myDrawnTile = T.tile(SuitView.WIND, 1, 1))
            val screen = screen(FakeGameSession(initialView = v))
            try {
                val groups = MeldDisplayLayout.layoutAll(screen.currentView)
                assertEquals(16, groups.size)
                val tiles = groups.flatMap { it.tiles }
                for (tile in tiles) {
                    assertTrue(tile.x >= 0 && tile.x + tile.width <= 1280)
                    assertTrue(tile.y >= 25 && tile.y + tile.height <= 655)
                    assertFalse(rect(tile).overlaps(Rectangle(230f, 145f, 970f, 44f)))
                    assertTrue(screen.refreshHandTileRects().none { it.rect.overlaps(rect(tile)) })
                }
                for (i in tiles.indices) for (j in i + 1 until tiles.size)
                    assertFalse(rect(tiles[i]).overlaps(rect(tiles[j])), "Tiles overlap: $i / $j, viewer $viewer")
            } finally { screen.dispose() }
        }
    }

    @Test fun `chi pon and every kan committed by another seat update the observer screen`() {
        for (type in MeldTypeView.entries) {
            val original = view(0, List(4) { emptyList() })
            val session = FakeGameSession(initialView = original)
            val screen = screen(session)
            try {
                assertTrue(MeldDisplayLayout.layoutAll(screen.currentView).isEmpty())
                val confirmed = meld(type, 2)
                val updated = original.copy(melds = List(4) { if (it == 2) listOf(confirmed) else emptyList() })
                session.update(updated)
                val shown = MeldDisplayLayout.layoutAll(screen.currentView).filter { it.ownerSeat == 2 }
                assertEquals(listOf(type), shown.map { it.type })
                assertEquals(confirmed.tiles, shown.single().tiles.map { it.tile })
                assertEquals(confirmed.tiles.size, shown.single().tiles.map { it.tile.tileId }.toSet().size)
                if (type != MeldTypeView.CLOSED_KAN) assertEquals(confirmed.calledTile, shown.single().tiles.single { it.called }.tile)
            } finally { screen.dispose() }
        }
    }

    @Test fun `opponent meld rendering uses public faces backs and labels near the owning group`() {
        val source = listOf(emptyList(), listOf(meld(MeldTypeView.CLOSED_KAN, 1)),
            listOf(meld(MeldTypeView.ADDED_KAN, 2)), listOf(meld(MeldTypeView.CHI, 3)))
        val groups = MeldDisplayLayout.layoutAll(view(0, source))
        val faces = mutableListOf<TileView>()
        var backs = 0
        val skin = object : TileSkin {
            override val id = SkinId.CLASSIC
            override val displayName = "recording"
            override fun appendTileDraw(buf: TileDrawBuffer, tile: TileView, x: Float, y: Float, width: Float, height: Float, isHover: Boolean, isDrawn: Boolean) { faces.add(tile) }
            override fun appendTileBackDraw(buf: TileDrawBuffer, x: Float, y: Float, width: Float, height: Float) { backs++ }
        }
        val buffer = TileDrawBuffer()
        MeldDisplayRenderer.append(buffer, skin, groups)
        assertEquals(2, backs)
        assertEquals(groups.flatMap { it.tiles }.filterNot { it.faceDown }.map { it.tile }, faces)
        assertTrue(buffer.textDraws.any { "ADD" in it.text })
        assertTrue(buffer.textDraws.any { "CALLED" in it.text })
        assertTrue(buffer.textDraws.all { it.y > 190 && it.y <= 668 })
        for (seat in 1..3) assertTrue(buffer.textDraws.any { "P$seat" in it.text })
    }

    @Test fun `opponent groups are noninteractive replace on updates and clear for a new round`() {
        val pon = meld(MeldTypeView.PON, 1)
        val v = view(0, listOf(emptyList(), listOf(pon), emptyList(), emptyList()))
        val session = FakeGameSession(initialView = v.copy(legalActions = v.myAllClosedTiles.map { LegalActionView.DiscardTile(0, it) }))
        val screen = screen(session)
        try {
            for (t in MeldDisplayLayout.layoutAll(screen.currentView).flatMap { it.tiles })
                screen.touchDown((t.x + t.width / 2).toInt(), (t.y + t.height / 2).toInt(), 0, 0)
            assertTrue(session.submitted.isEmpty())
            val old = MeldDisplayLayout.layoutAll(screen.currentView)
            session.update(v.copy(melds = listOf(emptyList(), listOf(meld(MeldTypeView.ADDED_KAN, 1)), emptyList(), emptyList())))
            assertEquals(MeldTypeView.ADDED_KAN, MeldDisplayLayout.layoutAll(screen.currentView).single().type)
            assertEquals(4, MeldDisplayLayout.layoutAll(screen.currentView).single().tiles.size)
            assertEquals(3, old.single().tiles.size)
            assertThrows(UnsupportedOperationException::class.java) { (old as MutableList).clear() }
            session.update(v.copy(melds = List(4) { emptyList() }, phase = GamePhaseView.STACKING))
            assertTrue(MeldDisplayLayout.layoutAll(screen.currentView).isEmpty())
        } finally { screen.dispose() }
    }

    @Test fun `initial client snapshot restores opponent physical melds without waiting for update`() {
        val pon = meld(MeldTypeView.PON, 1)
        val session = FakeGameSession(initialView = view(0, listOf(emptyList(), listOf(pon), emptyList(), emptyList())))
        val screen = screen(session)
        try {
            val shown = MeldDisplayLayout.layoutAll(screen.currentView).single()
            assertEquals(1, shown.ownerSeat)
            assertEquals(pon.tiles, shown.tiles.map { it.tile })
        } finally { screen.dispose() }
    }
}
