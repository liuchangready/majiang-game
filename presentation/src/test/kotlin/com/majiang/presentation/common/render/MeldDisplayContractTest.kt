package com.majiang.presentation.common.render

import com.majiang.client.api.GamePhaseView
import com.majiang.client.api.MeldTypeView
import com.majiang.client.api.MeldView
import com.majiang.client.api.PlayerView
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class MeldDisplayContractTest {

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

    private fun meld(type: MeldTypeView): MeldView {
        val tiles = when (type) {
            MeldTypeView.CHI -> listOf(tile(SuitView.MAN, 2), tile(SuitView.MAN, 3), tile(SuitView.MAN, 4))
            MeldTypeView.PON -> listOf(tile(SuitView.PIN, 7, 0), tile(SuitView.PIN, 7, 1), tile(SuitView.PIN, 7, 2))
            MeldTypeView.OPEN_KAN -> listOf(tile(SuitView.SOU, 8, 0), tile(SuitView.SOU, 8, 1), tile(SuitView.SOU, 8, 2), tile(SuitView.SOU, 8, 3))
            MeldTypeView.CLOSED_KAN -> listOf(tile(SuitView.DRAGON, 1, 0), tile(SuitView.DRAGON, 1, 1), tile(SuitView.DRAGON, 1, 2), tile(SuitView.DRAGON, 1, 3))
            MeldTypeView.ADDED_KAN -> listOf(tile(SuitView.WIND, 2, 0), tile(SuitView.WIND, 2, 1), tile(SuitView.WIND, 2, 2), tile(SuitView.WIND, 2, 3))
        }
        return MeldView(type, tiles, if (type == MeldTypeView.CLOSED_KAN) -1 else 1, tiles.first())
    }

    private fun createPlayerView(seat: Int = 2, seatMelds: List<MeldView>): PlayerView {
        val meldList = List(4) { if (it == seat) seatMelds else emptyList() }
        return PlayerView(
            viewerSeat = seat,
            phase = GamePhaseView.DISCARD,
            roundWind = 1,
            roundNumber = 0,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = seat,
            wallRemaining = 70,
            doraIndicators = emptyList(),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = List(4) { emptyList() },
            melds = meldList,
            handTileCounts = listOf(13, 13, 13, 13),
            myClosedTiles = (1..10).map { tile(SuitView.MAN, it % 9 + 1) },
            myDrawnTile = tile(SuitView.WIND, 4, 3),
            legalActions = emptyList()
        )
    }

    @Test
    fun `every meld type displays each exact physical tile once`() {
        for (type in MeldTypeView.entries) {
            val source = meld(type)
            val v = createPlayerView(2, listOf(source))
            val g = MeldDisplayLayout.layout(v).single()
            assertEquals(type, g.type)
            assertEquals(source.tiles, g.tiles.map { it.tile })
            assertEquals(if (type in listOf(MeldTypeView.CHI, MeldTypeView.PON)) 3 else 4, g.tiles.size)
            assertEquals(source.tiles.size, g.tiles.map { it.tile }.toSet().size)
            if (type == MeldTypeView.CLOSED_KAN) {
                assertNull(g.fromPlayer)
                assertTrue(g.tiles.none { it.called || it.added })
            } else {
                assertEquals(1, g.fromPlayer)
                assertEquals(source.calledTile, g.tiles.single { it.called }.tile)
            }
        }
    }

    @Test
    fun `closed kan hides outer tiles and added kan marks only appended fourth tile`() {
        val closed = MeldDisplayLayout.layout(createPlayerView(2, listOf(meld(MeldTypeView.CLOSED_KAN)))).single()
        assertEquals(listOf(true, false, false, true), closed.tiles.map { it.faceDown })
        val added = MeldDisplayLayout.layout(createPlayerView(2, listOf(meld(MeldTypeView.ADDED_KAN)))).single()
        assertTrue(added.tiles.none { it.faceDown })
        assertEquals(1, added.tiles.count { it.added })
        assertEquals(tile(SuitView.WIND, 2, 3), added.tiles.single { it.added }.tile)
        assertFalse(added.tiles.single { it.added }.called)
    }

    @Test
    fun `observer layout ignores other seats and never mutates input`() {
        val own = meld(MeldTypeView.CHI)
        val other = meld(MeldTypeView.PON)
        val v = createPlayerView(2, listOf(own)).copy(melds = listOf(listOf(other), emptyList(), listOf(own), emptyList()))
        val before = v.copy()
        val groups = MeldDisplayLayout.layout(v)
        assertEquals(listOf(MeldTypeView.CHI), groups.map { it.type })
        assertEquals(before, v)
        assertEquals(listOf(MeldTypeView.PON), MeldDisplayLayout.layout(v.copy(viewerSeat = 0)).map { it.type })
        assertTrue(MeldDisplayLayout.layout(v.copy(viewerSeat = 3)).isEmpty())
    }

    @Test
    fun `layout snapshots and nested tile lists are immutable`() {
        val source = meld(MeldTypeView.PON)
        val tiles = source.tiles.toMutableList()
        val base = createPlayerView(2, listOf(source))
        val list = mutableListOf(source.copy(tiles = tiles))
        val groups = MeldDisplayLayout.layout(base.copy(melds = listOf(emptyList(), emptyList(), list, emptyList())))
        tiles.clear()
        list.clear()
        assertEquals(3, groups.single().tiles.size)
        assertThrows(UnsupportedOperationException::class.java) { (groups as MutableList).clear() }
        assertThrows(UnsupportedOperationException::class.java) { (groups.single().tiles as MutableList).clear() }
    }

    @Test
    fun `one to four kans fit within 4-kan display boundary`() {
        val kans = listOf(SuitView.MAN, SuitView.PIN, SuitView.SOU, SuitView.DRAGON).map { suit ->
            val tiles = (0..3).map { tile(suit, 1, it) }
            MeldView(MeldTypeView.CLOSED_KAN, tiles, -1, tiles.first())
        }
        for (n in 1..4) {
            val v = createPlayerView(2, kans.take(n))
            val groups = MeldDisplayLayout.layout(v)
            assertEquals(n, groups.size)
            val all = groups.flatMap { it.tiles }
            assertTrue(all.all { it.x >= 220 && it.x + it.width <= 1048.01f && it.y >= 25 && it.y + it.height < 145 })
            for (a in all.indices) for (b in a + 1 until all.size) {
                val x = all[a]
                val y = all[b]
                assertFalse(
                    com.badlogic.gdx.math.Rectangle(x.x, x.y, x.width, x.height).overlaps(
                        com.badlogic.gdx.math.Rectangle(y.x, y.y, y.width, y.height)
                    )
                )
            }
            assertEquals(1048f, all.maxOf { it.x + it.width }, 0.01f)
        }
    }

    @Test
    fun `renderer uses real tile skin for faces and backs and labels called and added tiles`() {
        val groups = MeldDisplayLayout.layout(
            createPlayerView(
                2,
                listOf(
                    meld(MeldTypeView.CHI),
                    meld(MeldTypeView.PON),
                    meld(MeldTypeView.CLOSED_KAN),
                    meld(MeldTypeView.ADDED_KAN)
                )
            )
        )
        val faces = mutableListOf<TileView>()
        var backs = 0
        val skin = object : TileSkin {
            override val id = SkinId.CLASSIC
            override val displayName = "recording"
            override fun appendTileDraw(
                buf: TileDrawBuffer,
                tile: TileView,
                x: Float,
                y: Float,
                width: Float,
                height: Float,
                isHover: Boolean,
                isDrawn: Boolean,
                alpha: Float
            ) {
                faces.add(tile)
            }
            override fun appendTileBackDraw(buf: TileDrawBuffer, x: Float, y: Float, width: Float, height: Float) {
                backs++
            }
            override fun dispose() {}
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
}
