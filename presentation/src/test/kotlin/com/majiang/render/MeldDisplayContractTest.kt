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

class MeldDisplayContractTest : GdxHeadlessTestRunner() {
    private fun meld(type: MeldTypeView): MeldView {
        val tiles = when (type) {
            MeldTypeView.CHI -> listOf(2, 3, 4).map { T.tile(SuitView.MAN, it) }
            MeldTypeView.PON -> (0..2).map { T.tile(SuitView.PIN, 7, it) }
            MeldTypeView.OPEN_KAN -> (0..3).map { T.tile(SuitView.SOU, 8, it) }
            MeldTypeView.CLOSED_KAN -> (0..3).map { T.tile(SuitView.DRAGON, 1, it) }
            MeldTypeView.ADDED_KAN -> (0..3).map { T.tile(SuitView.WIND, 2, it) }
        }
        return MeldView(type, tiles, if (type == MeldTypeView.CLOSED_KAN) -1 else 1, tiles.first())
    }
    private fun view(melds: List<MeldView>, seat: Int = 2): PlayerView =
        T.playerView(viewerSeat = seat, melds = List(4) { if (it == seat) melds else emptyList() })
    private fun screen(session: FakeGameSession): GameScreen = GameScreen(TestUiAssets.create(), session, FakeTableNavigation())

    @Test fun `every meld type displays each exact physical tile once`() {
        for (type in MeldTypeView.entries) {
            val source = meld(type)
            val group = MeldDisplayLayout.layout(view(listOf(source))).single()
            assertEquals(type, group.type)
            assertEquals(source.tiles, group.tiles.map { it.tile })
            assertEquals(if (type == MeldTypeView.CHI || type == MeldTypeView.PON) 3 else 4, group.tiles.size)
            assertEquals(source.tiles.size, group.tiles.map { it.tile.tileId }.toSet().size)
            if (type == MeldTypeView.CLOSED_KAN) {
                assertNull(group.fromPlayer)
                assertTrue(group.tiles.none { it.called || it.added })
            } else {
                assertEquals(1, group.fromPlayer)
                assertEquals(source.calledTile, group.tiles.single { it.called }.tile)
            }
        }
    }
    @Test fun `closed kan hides outer tiles and added kan marks only appended fourth tile`() {
        val closed = MeldDisplayLayout.layout(view(listOf(meld(MeldTypeView.CLOSED_KAN)))).single()
        assertEquals(listOf(true, false, false, true), closed.tiles.map { it.faceDown })
        val added = MeldDisplayLayout.layout(view(listOf(meld(MeldTypeView.ADDED_KAN)))).single()
        assertTrue(added.tiles.none { it.faceDown })
        assertEquals(1, added.tiles.count { it.added })
        assertEquals(T.tile(SuitView.WIND, 2, 3), added.tiles.single { it.added }.tile)
        assertFalse(added.tiles.single { it.added }.called)
    }
    @Test fun `observer layout ignores other seats and never mutates input`() {
        val own = meld(MeldTypeView.CHI)
        val other = meld(MeldTypeView.PON)
        val v = view(listOf(own)).copy(melds = listOf(listOf(other), emptyList(), listOf(own), emptyList()))
        val before = v.copy()
        val groups = MeldDisplayLayout.layout(v)
        assertEquals(listOf(MeldTypeView.CHI), groups.map { it.type })
        assertEquals(before, v)
        assertEquals(listOf(MeldTypeView.PON), MeldDisplayLayout.layout(v.copy(viewerSeat = 0)).map { it.type })
        assertTrue(MeldDisplayLayout.layout(v.copy(viewerSeat = 3)).isEmpty())
    }
    @Test fun `layout snapshots and nested tile lists are immutable`() {
        val source = meld(MeldTypeView.PON)
        val tiles = source.tiles.toMutableList()
        val list = mutableListOf(MeldView(source.type, tiles, source.fromPlayer, source.calledTile))
        val groups = MeldDisplayLayout.layout(view(emptyList()).copy(melds = listOf(emptyList(), emptyList(), list, emptyList())))
        tiles.clear(); list.clear()
        assertEquals(3, groups.single().tiles.size)
        assertThrows(UnsupportedOperationException::class.java) { (groups as MutableList).clear() }
        assertThrows(UnsupportedOperationException::class.java) { (groups.single().tiles as MutableList).clear() }
    }
    @Test fun `one to four kans fit beside actual hand slots with sorting on and off`() {
        val kans = listOf(SuitView.MAN, SuitView.PIN, SuitView.SOU, SuitView.DRAGON).map { suit ->
            val tiles = (0..3).map { T.tile(suit, 1, it) }
            MeldView(MeldTypeView.CLOSED_KAN, tiles, -1, tiles.first())
        }
        for (n in 1..4) {
            val base = view(kans.take(n))
            val screen = screen(FakeGameSession(initialView = base.copy(
                myClosedTiles = base.myClosedTiles.take(13 - 3 * n), myDrawnTile = null)))
            repeat(2) {
                val hand = screen.refreshHandTileRects()
                val groups = MeldDisplayLayout.layout(screen.currentView)
                assertEquals(n, groups.size)
                val all = groups.flatMap { it.tiles }
                assertTrue(all.all { it.x >= 220 && it.x + it.width <= 1048.01f && it.y >= 25 && it.y + it.height < 145 })
                assertTrue(hand.all { h -> all.none { t -> h.rect.overlaps(Rectangle(t.x, t.y, t.width, t.height)) } })
                for (a in all.indices) for (b in a + 1 until all.size) {
                    val x = all[a]; val y = all[b]
                    assertFalse(Rectangle(x.x, x.y, x.width, x.height).overlaps(Rectangle(y.x, y.y, y.width, y.height)))
                }
                assertEquals(1048f, all.maxOf { it.x + it.width }, 0.01f)
                val before = groups
                screen.touchDown(1140, 75, 0, 0)
                assertEquals(before, MeldDisplayLayout.layout(screen.currentView))
            }
            screen.dispose()
        }
    }
    @Test fun `renderer uses real tile skin for faces and backs and labels called and added tiles`() {
        val groups = MeldDisplayLayout.layout(view(listOf(meld(MeldTypeView.CHI), meld(MeldTypeView.PON), meld(MeldTypeView.CLOSED_KAN), meld(MeldTypeView.ADDED_KAN))))
        val faces = mutableListOf<TileView>(); var backs = 0
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
        assertTrue(buffer.textDraws.any { "CALLED" in it.text })
        assertTrue(buffer.textDraws.any { "ADD" in it.text })
        assertTrue(buffer.textDraws.any { "CLOSED" in it.text })
        assertTrue(buffer.textDraws.any { "FROM" in it.text })
    }
    @Test fun `completed meld tiles are not clickable discards and updates clear stale groups`() {
        val base = view(listOf(meld(MeldTypeView.PON)))
        val v = base.copy(myClosedTiles = base.myClosedTiles.take(10), myDrawnTile = null)
        val session = FakeGameSession(initialView = v.copy(legalActions = v.myAllClosedTiles.map { LegalActionView.DiscardTile(2, it) }))
        val screen = screen(session)
        for (tile in MeldDisplayLayout.layout(screen.currentView).flatMap { it.tiles })
            screen.touchDown((tile.x + tile.width / 2).toInt(), (tile.y + tile.height / 2).toInt(), 0, 0)
        assertTrue(session.submitted.isEmpty())
        session.update(v.copy(melds = List(4) { emptyList() }))
        assertTrue(MeldDisplayLayout.layout(screen.currentView).isEmpty())
        screen.dispose()
    }
    @Test fun `confirmed chi pon and all kan types appear only after authoritative view update`() {
        for (type in MeldTypeView.entries) {
            val expected = meld(type)
            val action: LegalActionView = when (type) {
                MeldTypeView.CHI -> LegalActionView.DeclareChi(2, expected)
                MeldTypeView.PON -> LegalActionView.DeclarePon(2, expected.tiles.drop(1))
                else -> LegalActionView.DeclareKan(2, if (type == MeldTypeView.OPEN_KAN) expected.tiles.drop(1) else expected.tiles, type)
            }
            val phase = if (type == MeldTypeView.CHI || type == MeldTypeView.PON || type == MeldTypeView.OPEN_KAN) GamePhaseView.RESPONSE_WINDOW else GamePhaseView.DISCARD
            val initial = view(emptyList()).copy(phase = phase, legalActions = listOf(action), lastDiscard = expected.calledTile, lastDiscardPlayer = 1)
            val session = FakeGameSession(initialView = initial)
            val screen = screen(session)
            val x = when (type) { MeldTypeView.CHI -> 500; MeldTypeView.PON -> 640; else -> 350 }
            assertTrue(screen.touchDown(x, 160, 0, 0))
            val index = screen.meldSelectionModel.options.indexOfFirst { it.type == type }
            assertTrue(index >= 0)
            assertTrue(screen.touchDown(270 + 255 * index, 350, 0, 0))
            assertTrue(screen.touchDown(720, 160, 0, 0))
            assertEquals(listOf(action), session.submitted)
            assertTrue(MeldDisplayLayout.layout(screen.currentView).isEmpty())
            session.update(initial.copy(melds = List(4) { if (it == 2) listOf(expected) else emptyList() }, legalActions = emptyList()))
            val actual = MeldDisplayLayout.layout(screen.currentView)
            assertEquals(listOf(type), actual.map { it.type })
            assertEquals(expected.tiles, actual.single().tiles.map { it.tile })
            assertEquals(expected.tiles.size, actual.single().tiles.map { it.tile.tileId }.toSet().size)
            screen.dispose()
        }
    }
}
