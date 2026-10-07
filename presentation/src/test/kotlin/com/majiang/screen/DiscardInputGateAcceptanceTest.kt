package com.majiang.screen

import com.majiang.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.common.render.DiscardAnimation
import com.majiang.presentation.game.GameScreen
import com.majiang.testing.FakeGameSession
import com.majiang.testing.FakeTableNavigation
import com.majiang.testing.TestUiAssets
import com.majiang.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Codex-owned regression: the authoritative next turn can arrive before presentation catches up. */
class DiscardInputGateAcceptanceTest : GdxHeadlessTestRunner() {

    private fun view(): PlayerView {
        val closed = (1..9).map { TestViewBuilder.tile(SuitView.SOU, it) } + (1..4).map { TestViewBuilder.tile(SuitView.PIN, it) }
        val drawn = TestViewBuilder.tile(SuitView.PIN, 5)
        val allClosed = closed + drawn
        val legal = allClosed.map { LegalActionView.DiscardTile(2, it) }
        return TestViewBuilder.playerView(
            viewerSeat = 2,
            currentTurnPlayer = 2,
            phase = GamePhaseView.DISCARD,
            myClosedTiles = closed,
            myDrawnTile = drawn,
            legalActions = legal
        )
    }

    private fun addDiscards(v: PlayerView, count: Int = 1): PlayerView = v.copy(
        discards = v.discards.mapIndexed { seat, old ->
            if (seat < count) old + TestViewBuilder.tile(SuitView.MAN, seat + 1) else old
        }
    )

    private fun clickTile(s: GameScreen, t: TileView? = null) {
        val r = s.refreshHandTileRects().first { t == null || it.tile == t }.rect
        s.touchDown((r.x + r.width / 2).toInt(), 80, 0, 0)
    }

    private fun scene(v: PlayerView = view()): Pair<GameScreen, FakeGameSession> {
        val f = FakeGameSession(initialView = v)
        val nav = FakeTableNavigation()
        val s = GameScreen(TestUiAssets.create(), f, nav)
        s.startGame()
        return s to f
    }

    @Test fun `synchronous next-turn snapshot blocks a second click before any render`() {
        val v = view()
        val (s, fake) = scene(v)
        clickTile(s)
        assertEquals(1, fake.submitted.size)
        // Simulate next discard arriving immediately
        fake.update(addDiscards(fake.snapshot.view))
        // No render/checkDiscardsDiff call between two input events.
        clickTile(s)
        clickTile(s)
        assertEquals(1, fake.submitted.size, "The next legal turn must not bypass pending playback")
        assertTrue(s.discardQueue.isNotEmpty() || s.currentDiscardAnim != null)
        s.dispose()
    }

    @Test fun `all three queued discards must finish before hand and sort unlock`() {
        val (s, f) = scene()
        f.update(addDiscards(f.snapshot.view, 3))
        s.checkDiscardsDiff()
        repeat(3) {
            clickTile(s); s.touchDown(1140, 75, 0, 0)
            assertTrue(f.submitted.isEmpty(), "No early discard at animation $it")
            assertFalse(s.isAutoSort, "Sort must not operate hand during playback")
            s.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION + 0.01f)
        }
        assertNull(s.currentDiscardAnim); assertTrue(s.discardQueue.isEmpty())
        assertTrue(f.submitted.isEmpty(), "Blocked clicks are discarded, never replayed")
        clickTile(s); assertEquals(1, f.submitted.size)
        f.flushUi()
        s.touchDown(1140, 75, 0, 0); assertTrue(s.isAutoSort)
        s.dispose()
    }

    @Test fun `response controls wait for animation then ron remains selectable`() {
        val v = view().copy(
            phase = GamePhaseView.RESPONSE_WINDOW,
            currentTurnPlayer = 1,
            legalActions = listOf(LegalActionView.DeclareRon(2), LegalActionView.Pass(2))
        )
        val (s, f) = scene(v)
        f.update(addDiscards(v))
        assertFalse(s.canHumanRon()); assertFalse(s.canHumanPass())
        s.touchDown(750, 160, 0, 0); s.touchDown(900, 160, 0, 0); clickTile(s)
        assertTrue(f.submitted.isEmpty())
        s.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION + 0.01f)
        assertTrue(s.canHumanRon()); assertTrue(s.canHumanPass())
        s.touchDown(750, 160, 0, 0)
        assertEquals(listOf(LegalActionView.DeclareRon(2)), f.submitted)
        s.dispose()
    }

    @Test fun `my turn alone cannot submit a discard without a legal discard action`() {
        val (s, fake) = scene(view().copy(legalActions = listOf(LegalActionView.DeclareTsumo(2))))
        clickTile(s)
        assertTrue(fake.submitted.isEmpty(), "Do not even submit unauthorized hand clicks")
        s.touchDown(900, 160, 0, 0)
        assertEquals(listOf(LegalActionView.DeclareTsumo(2)), fake.submitted)
        s.dispose()
    }

    @Test fun `an already open meld confirmation cannot bypass the playback gate`() {
        val called = TestViewBuilder.tile(SuitView.DRAGON, 1, 2)
        val ponTiles = listOf(TestViewBuilder.tile(SuitView.DRAGON, 1, 0), TestViewBuilder.tile(SuitView.DRAGON, 1, 1))
        val v = TestViewBuilder.playerView(
            viewerSeat = 2,
            phase = GamePhaseView.RESPONSE_WINDOW,
            currentTurnPlayer = 1,
            lastDiscard = called,
            lastDiscardPlayer = 1,
            legalActions = listOf(LegalActionView.DeclarePon(2, ponTiles), LegalActionView.Pass(2))
        )
        val (s, f) = scene(v)
        s.touchDown(640, 160, 0, 0)
        assertTrue(s.meldSelectionModel.isSelecting)
        val intended = s.meldSelectionModel.confirmationOption()!!.action
        val t = TestViewBuilder.tile(SuitView.MAN, 1)
        s.discardQueue.add(GameScreen.QueuedDiscard(1, t, 0, s.getDiscardStart(1), s.getDiscardSlotPosition(1, 0)))
        s.touchDown(720, 160, 0, 0)
        assertTrue(f.submitted.isEmpty())
        s.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION + 0.01f)
        s.touchDown(720, 160, 0, 0)
        assertEquals(listOf(intended), f.submitted)
        s.dispose()
    }

    @Test fun `all turn buttons report unavailable during playback without deleting legal actions`() {
        val v = view()
        val tile = v.myAllClosedTiles.first()
        val actions = listOf(
            LegalActionView.DeclareTsumo(2),
            LegalActionView.DeclareRiichi(2, tile),
            LegalActionView.DeclareAbortiveDraw(2),
            LegalActionView.DeclarePon(2, listOf(tile, TestViewBuilder.tile(tile.suit, tile.rank, 1))),
            LegalActionView.DeclareChi(2, TestViewBuilder.meld(MeldTypeView.CHI, listOf(tile, TestViewBuilder.tile(SuitView.SOU, 2), TestViewBuilder.tile(SuitView.SOU, 3)), 1, tile)),
            LegalActionView.DeclareKan(2, (0..3).map { TestViewBuilder.tile(SuitView.DRAGON, 1, it) }, MeldTypeView.CLOSED_KAN)
        )
        val (s, f) = scene(v.copy(legalActions = actions))
        f.update(addDiscards(f.snapshot.view))
        assertFalse(s.canHumanTsumo()); assertFalse(s.canHumanRiichi()); assertFalse(s.canHumanAbortiveDraw())
        assertFalse(s.canHumanPon()); assertFalse(s.canHumanChi()); assertFalse(s.canHumanKan())
        assertEquals(actions, f.snapshot.view.legalActions, "UI lock must not mutate domain eligibility")
        s.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION + 0.01f)
        assertTrue(s.canHumanTsumo()); assertTrue(s.canHumanRiichi()); assertTrue(s.canHumanAbortiveDraw())
        assertTrue(s.canHumanPon()); assertTrue(s.canHumanChi()); assertTrue(s.canHumanKan())
        s.dispose()
    }

    @Test fun `claimed queued tile is removed and cannot keep input locked`() {
        val (s, f) = scene()
        val before = f.snapshot.view
        f.update(addDiscards(before, 2))
        s.checkDiscardsDiff()
        s.updateDiscardAnimation(0.1f)
        assertEquals(1, s.discardQueue.size)
        f.update(f.snapshot.view.copy(discards = f.snapshot.view.discards.mapIndexed { i, tiles -> if (i == 1) emptyList() else tiles }))
        s.checkDiscardsDiff()
        assertTrue(s.discardQueue.isEmpty(), "Claimed unplayed animation must be pruned")
        s.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION)
        assertNull(s.currentDiscardAnim)
        clickTile(s)
        assertEquals(1, f.submitted.size)
        s.dispose()
    }

    @Test fun `new round and settlement clear playback rather than stranding controls`() {
        val (s, f) = scene()
        val base = f.snapshot.view
        f.update(addDiscards(base, 3))
        s.checkDiscardsDiff()
        s.updateDiscardAnimation(0.1f)
        f.update(base, listOf(GameEventView.StackingStarted()))
        assertTrue(s.discardQueue.isEmpty()); assertNull(s.currentDiscardAnim)
        clickTile(s); assertEquals(1, f.submitted.size)
        f.update(addDiscards(base), listOf(GameEventView.TsumoWin(1, 2, TestViewBuilder.tile(SuitView.PIN, 5), listOf("自摸"), 1, 30, 1500)))
        assertTrue(s.isSettlementShown); assertTrue(s.discardQueue.isEmpty()); assertNull(s.currentDiscardAnim)
        s.touchDown(640, 165, 0, 0)
        assertTrue(s.isSettlementShown, "A next-round receipt alone must not discard settlement")
        f.update(base.copy(phase = GamePhaseView.STACKING), listOf(GameEventView.StackingStarted()))
        assertFalse(s.isSettlementShown)
        assertTrue(s.discardQueue.isEmpty()); assertNull(s.currentDiscardAnim)
        s.dispose()
    }

    @Test fun `new playback immediately clears a previously highlighted hand tile`() {
        val (s, f) = scene()
        val slot = s.refreshHandTileRects().first()
        val field = GameScreen::class.java.getDeclaredField("hoveredTile").apply { isAccessible = true }
        s.mouseMoved((slot.rect.x + 20).toInt(), 80)
        assertEquals(slot.tile, field.get(s))
        f.update(addDiscards(f.snapshot.view))
        assertNull(field.get(s), "No misleading hover between update and next render")
        s.mouseMoved((slot.rect.x + 20).toInt(), 80); assertNull(field.get(s))
        s.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION + 0.01f)
        s.mouseMoved((slot.rect.x + 20).toInt(), 80); assertEquals(slot.tile, field.get(s))
        s.dispose()
    }

    @Test fun `selected riichi confirmation is blocked until pending playback completes`() {
        val east = TestViewBuilder.tile(SuitView.WIND, 1, 1)
        val river = TestViewBuilder.tile(SuitView.MAN, 9, 3)
        val closed = (1..6).map { TestViewBuilder.tile(SuitView.MAN, it) } +
            (7..9).map { TestViewBuilder.tile(SuitView.PIN, it) } +
            (1..3).map { TestViewBuilder.tile(SuitView.SOU, it) } +
            TestViewBuilder.tile(SuitView.WIND, 1, 0)
        val action = LegalActionView.DeclareRiichi(2, east)
        val view = TestViewBuilder.playerView(
            viewerSeat = 2,
            currentTurnPlayer = 2,
            phase = GamePhaseView.DISCARD,
            myClosedTiles = closed,
            myDrawnTile = east,
            discards = listOf(listOf(river), emptyList(), emptyList(), emptyList()),
            legalActions = listOf(action)
        )
        val (s, f) = scene(view)
        s.touchDown(720, 160, 0, 0); assertTrue(s.riichiSelectionModel.isSelecting)
        clickTile(s, east); assertEquals(action, s.riichiSelectionModel.confirmationOption()?.action)
        s.discardQueue.add(GameScreen.QueuedDiscard(0, river, 0, s.getDiscardStart(0), s.getDiscardSlotPosition(0, 0)))
        s.touchDown(720, 160, 0, 0); assertTrue(f.submitted.isEmpty())
        s.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION + 0.01f)
        s.touchDown(720, 160, 0, 0); assertEquals(listOf(action), f.submitted)
        s.dispose()
    }
}
