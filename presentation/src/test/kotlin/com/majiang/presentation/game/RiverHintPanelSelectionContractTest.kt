package com.majiang.presentation.game

import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.common.render.ClassicSkin
import com.majiang.presentation.common.render.TileDrawBuffer
import com.majiang.presentation.common.render.TileSkin
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Only rendering hit boxes are injected; clicks and snapshot reconciliation use the real panel. */
class RiverHintPanelSelectionContractTest : GdxHeadlessTestRunner() {
    private fun snapshot(
        riverIds: List<Int> = emptyList(),
        markedArea: TileMarkArea? = null,
        roundId: String = "round",
        sessionId: String = "session",
        exposedMeld: Boolean = false
    ): GameSnapshotView {
        val groups = if (markedArea == null) emptyList() else {
            val selected = MarkedTileView(0, TestViewBuilder.tile(0), markedArea,
                if (markedArea == TileMarkArea.NOT_VISIBLE) null else 1,
                if (markedArea == TileMarkArea.DISCARD) RiverPositionView(roundId, 1, riverIds.indexOf(0)) else null)
            listOf(TileMarkGroupView("group", "first-round", 1, 4, 3, false,
                listOf(selected) + (1..3).map { MarkedTileView(it, TestViewBuilder.tile(it), TileMarkArea.NOT_VISIBLE) }))
        }
        return GameSnapshotView(sessionId, roundId, "epoch", 1, 1, 1,
            TestViewBuilder.playerView(viewerSeat = 0,
                discards = listOf(emptyList(), riverIds.map(TestViewBuilder::tile), emptyList(), emptyList()),
                melds = listOf(emptyList(), if (exposedMeld) listOf(TestViewBuilder.meld(MeldTypeView.CHI,
                    listOf(0, 4, 8).map(TestViewBuilder::tile))) else emptyList(), emptyList(), emptyList())),
            DecisionView(roundId, "decision", 0), tileMark = TileMarkView(false, "NOT_YOUR_TURN",
                activeTileCount = groups.size * 4, decisionId = "decision", controlEpoch = 0,
                stateVersion = 1, activeGroups = groups))
    }

    private fun select(panel: RiverHintPanel, tileId: Int = 0, hand: Boolean = false): Rectangle {
        // Keep injected hand targets in the fixed hand area, away from the draggable river region.
        val targetY = if (hand) panel.panelRect.y + panel.panelRect.height - 95f else panel.panelRect.y + 30f
        val rect = Rectangle(panel.panelRect.x + 30f, targetY, 20f, 27f)
        val field = RiverHintPanel::class.java.getDeclaredField(if (hand) "markedSlotClickTargets" else "riverTileClickTargets")
        field.isAccessible = true
        @Suppress("UNCHECKED_CAST")
        val targets = field.get(panel) as MutableList<Pair<Rectangle, Int>>
        targets.clear()
        targets.add(rect to tileId)
        panel.open()
        assertTrue(panel.touchDown(rect.x + 1f, rect.y + 1f))
        assertEquals(tileId, panel.selectedTileId)
        return rect
    }

    @Test
    fun `public unmarked river entity remains selected on subsequent authoritative snapshots`() {
        val panel = RiverHintPanel()
        val state = snapshot(riverIds = listOf(0, 4))
        panel.update(state)
        select(panel)
        panel.update(state.copy(cursor = 2))
        assertEquals(0, panel.selectedTileId)
        panel.update(state.copy(cursor = 3, tileMark = null))
        assertEquals(0, panel.selectedTileId, "Public river location does not require a private mark")
    }

    @Test
    fun `same type replacement is not the selected physical river entity`() {
        val panel = RiverHintPanel()
        panel.update(snapshot(riverIds = listOf(0)))
        select(panel)
        panel.update(snapshot(riverIds = listOf(1)))
        assertNull(panel.selectedTileId)
    }

    @Test
    fun `marked concealed entity follows its authorized move to this rounds river`() {
        val panel = RiverHintPanel()
        panel.update(snapshot(markedArea = TileMarkArea.OPPONENT_HAND))
        select(panel, hand = true)
        panel.update(snapshot(riverIds = listOf(0), markedArea = TileMarkArea.DISCARD))
        assertEquals(0, panel.selectedTileId)
    }

    @Test
    fun `marked river entity follows its exact physical tile into an exposed meld`() {
        val panel = RiverHintPanel()
        panel.update(snapshot(riverIds = listOf(0), markedArea = TileMarkArea.DISCARD))
        select(panel)
        panel.update(snapshot(markedArea = TileMarkArea.EXPOSED_MELD, exposedMeld = true))
        assertEquals(0, panel.selectedTileId, "The exposed meld takes over the entity location after a call")
        panel.update(snapshot(markedArea = TileMarkArea.NOT_VISIBLE))
        assertNull(panel.selectedTileId)
    }

    @Test
    fun `ordinary public river entity follows a public meld without a private mark`() {
        val panel = RiverHintPanel()
        panel.update(snapshot(riverIds = listOf(0)))
        select(panel)
        panel.update(snapshot(exposedMeld = true))
        assertEquals(0, panel.selectedTileId)
        panel.update(snapshot())
        assertNull(panel.selectedTileId, "An entity without any currently authorized visible position loses focus")
    }

    @Test
    fun `new round clears previous focus even when the same entity is visible again`() {
        val panel = RiverHintPanel()
        panel.update(snapshot(markedArea = TileMarkArea.OPPONENT_HAND))
        select(panel, hand = true)
        panel.update(snapshot(markedArea = TileMarkArea.OPPONENT_HAND, roundId = "next-round"))
        assertNull(panel.selectedTileId)
    }

    @Test
    fun `new session clears previous focus even with matching round and physical id`() {
        val panel = RiverHintPanel()
        panel.update(snapshot(markedArea = TileMarkArea.OPPONENT_HAND))
        select(panel, hand = true)
        panel.update(snapshot(markedArea = TileMarkArea.OPPONENT_HAND, sessionId = "another-session"))
        assertNull(panel.selectedTileId)
    }

    @Test
    fun `hidden expired and absent snapshots remove concealed entity focus`() {
        for (next in listOf(snapshot(markedArea = TileMarkArea.NOT_VISIBLE), snapshot(), null)) {
            val panel = RiverHintPanel()
            panel.update(snapshot(markedArea = TileMarkArea.OPPONENT_HAND))
            select(panel, hand = true)
            panel.update(next)
            assertNull(panel.selectedTileId)
        }
    }

    @Test
    fun `second entity click clears focus and empty panel clicks are consumed`() {
        val panel = RiverHintPanel()
        panel.update(snapshot(markedArea = TileMarkArea.OPPONENT_HAND))
        assertFalse(panel.isOpen)
        val target = select(panel, hand = true)
        assertTrue(panel.touchDown(target.x + 1f, target.y + 1f))
        assertNull(panel.selectedTileId)
        assertTrue(panel.touchDown(panel.panelRect.x + 1f, panel.panelRect.y + 1f))
        assertNull(panel.selectedTileId)
        panel.close()
        assertFalse(panel.touchDown(target.x + 1f, target.y + 1f))
    }

    @Test
    fun `animation gate suppresses only its exact river entity and removes its hit target`() {
        val panel = RiverHintPanel()
        val state = snapshot(riverIds = listOf(0, 4), markedArea = TileMarkArea.DISCARD)
        val renderedFaces = mutableListOf<Pair<Int, Float>>()
        val skin = object : TileSkin by ClassicSkin() {
            override fun appendTileDraw(buf: TileDrawBuffer, tile: TileView, x: Float, y: Float,
                width: Float, height: Float, isHover: Boolean, isDrawn: Boolean, alpha: Float) {
                renderedFaces.add(tile.tileId to alpha)
            }
        }
        val camera = OrthographicCamera(1280f, 720f)
        val batch = SpriteBatch()
        val shape = ShapeRenderer()
        val font = BitmapFont()
        try {
            panel.update(state)
            panel.open()
            val examined = mutableListOf<Triple<Int, Int, Int>>()
            panel.render(camera, shape, batch, font, state, skin, TileDrawBuffer()) { seat, tile, index ->
                examined.add(Triple(seat, tile.tileId, index))
                !(seat == 1 && tile.tileId == 0 && index == 0)
            }
            assertTrue(examined.contains(Triple(1, 0, 0)))
            assertTrue(examined.contains(Triple(1, 4, 1)))
            assertEquals(listOf(4 to 1f), renderedFaces)
            val field = RiverHintPanel::class.java.getDeclaredField("riverTileClickTargets")
            field.isAccessible = true
            @Suppress("UNCHECKED_CAST")
            val targets = field.get(panel) as List<Pair<Rectangle, Int>>
            assertEquals(listOf(4), targets.map { it.second })
            assertEquals(70, state.view.wallRemaining)

            renderedFaces.clear()
            panel.render(camera, shape, batch, font, state, skin, TileDrawBuffer()) { _, _, _ -> true }
            assertEquals(listOf(0 to 0.55f, 4 to 1f), renderedFaces)
            assertEquals(listOf(0, 4), targets.map { it.second })
            assertEquals(70, state.view.wallRemaining)
        } finally { panel.dispose(); font.dispose(); shape.dispose(); batch.dispose() }
    }
}
