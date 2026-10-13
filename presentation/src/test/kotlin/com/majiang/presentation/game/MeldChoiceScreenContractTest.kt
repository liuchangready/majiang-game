package com.majiang.presentation.game

import com.badlogic.gdx.Input
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.testing.FakeGameSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.presentation.testing.TestUiAssets
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture

class MeldChoiceScreenContractTest : GdxHeadlessTestRunner() {

    private fun click(s: GameScreen, x: Int, y: Int) = assertTrue(s.touchDown(x, y, 0, 0))
    private fun card(s: GameScreen, i: Int) = click(s, 270 + 255 * i, 350)
    private fun confirm(s: GameScreen) = click(s, 720, 160)

    private fun chiFixture(): Pair<GameScreen, FakeGameSession> {
        val called = TestViewBuilder.tile(SuitView.MAN, 5, 0)
        val chi1 = TestViewBuilder.meld(MeldTypeView.CHI, listOf(TestViewBuilder.tile(SuitView.MAN, 3), TestViewBuilder.tile(SuitView.MAN, 4), called), fromPlayer = 1, calledTile = called)
        val chi2 = TestViewBuilder.meld(MeldTypeView.CHI, listOf(TestViewBuilder.tile(SuitView.MAN, 4), called, TestViewBuilder.tile(SuitView.MAN, 6)), fromPlayer = 1, calledTile = called)
        val chi3 = TestViewBuilder.meld(MeldTypeView.CHI, listOf(called, TestViewBuilder.tile(SuitView.MAN, 6), TestViewBuilder.tile(SuitView.MAN, 7)), fromPlayer = 1, calledTile = called)
        val actions = listOf(
            LegalActionView.DeclareChi(2, chi1),
            LegalActionView.DeclareChi(2, chi2),
            LegalActionView.DeclareChi(2, chi3),
            LegalActionView.Pass(2)
        )
        val view = TestViewBuilder.playerView(
            viewerSeat = 2,
            phase = GamePhaseView.RESPONSE_WINDOW,
            currentTurnPlayer = 1,
            lastDiscard = called,
            lastDiscardPlayer = 1,
            legalActions = actions
        )
        val session = FakeGameSession(initialView = view)
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        return screen to session
    }

    private fun ponRonFixture(): Pair<GameScreen, FakeGameSession> {
        val called = TestViewBuilder.tile(SuitView.DRAGON, 1, 2)
        val ponTiles = listOf(TestViewBuilder.tile(SuitView.DRAGON, 1, 0), TestViewBuilder.tile(SuitView.DRAGON, 1, 1))
        val actions = listOf(
            LegalActionView.DeclarePon(2, ponTiles),
            LegalActionView.DeclareRon(2),
            LegalActionView.Pass(2)
        )
        val view = TestViewBuilder.playerView(
            viewerSeat = 2,
            phase = GamePhaseView.RESPONSE_WINDOW,
            currentTurnPlayer = 1,
            lastDiscard = called,
            lastDiscardPlayer = 1,
            legalActions = actions
        )
        val session = FakeGameSession(initialView = view)
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        return screen to session
    }

    @Test fun `each of three chi combinations can be independently previewed then committed`() {
        for (i in 0..2) {
            val (s, session) = chiFixture()
            click(s, 500, 160)
            assertTrue(s.meldSelectionModel.isSelecting)
            confirm(s)
            assertTrue(session.submitted.isEmpty())
            card(s, i)
            assertTrue(session.submitted.isEmpty())
            confirm(s)
            val expected = session.snapshot.decision.actions.filterIsInstance<LegalActionView.DeclareChi>()[i]
            assertEquals(listOf(expected), session.submitted)
            assertFalse(s.meldSelectionModel.isSelecting)
            s.dispose()
        }
    }

    @Test fun `cancel and back preserve ron and do not record missed win`() {
        for (mode in 0..2) {
            val (s, session) = ponRonFixture()
            assertTrue(s.canHumanRon())
            click(s, 640, 160)
            when (mode) {
                0 -> click(s, 1100, 160)
                1 -> assertTrue(s.keyDown(Input.Keys.ESCAPE))
                else -> assertTrue(s.keyDown(Input.Keys.BACK))
            }
            assertFalse(s.meldSelectionModel.isSelecting)
            assertTrue(s.canHumanRon())
            assertTrue(session.submitted.isEmpty())
            click(s, 640, 160)
            s.dispose()
        }
    }

    @Test fun `confirmed pon consumes two owned tiles and submits action`() {
        val (s, session) = ponRonFixture()
        click(s, 640, 160)
        assertNotNull(s.meldSelectionModel.confirmationOption())
        confirm(s)
        val expected = session.snapshot.decision.actions.filterIsInstance<LegalActionView.DeclarePon>().single()
        assertEquals(listOf(expected), session.submitted)
        s.dispose()
    }

    @Test fun `hover sorting and hand clicks cannot replace selected action or accidentally discard`() {
        val (s, session) = chiFixture()
        click(s, 500, 160)
        card(s, 2)
        val expected = s.meldSelectionModel.selectedOption
        s.mouseMoved(270, 350)
        click(s, 1140, 75)
        click(s, 245, 80)
        assertTrue(session.submitted.isEmpty())
        assertEquals(expected, s.meldSelectionModel.selectedOption)
        confirm(s)
        assertEquals(1, session.submitted.size)
        s.touchDown(720, 160, 0, 0)
        assertEquals(1, session.submitted.size)
        s.dispose()
    }

    @Test fun `explicit modal pass is the only preview action that submits pass`() {
        val (s, session) = ponRonFixture()
        click(s, 640, 160)
        click(s, 1100, 160)
        assertTrue(session.submitted.isEmpty())
        click(s, 640, 160)
        click(s, 900, 160)
        assertEquals(listOf(LegalActionView.Pass(2)), session.submitted)
        assertFalse(s.meldSelectionModel.isSelecting)
        s.dispose()
    }

    @Test fun `rejected and silently stale confirmations exit without committing another candidate`() {
        for (stale in listOf(false, true)) {
            val (s, session) = chiFixture()
            if (!stale) {
                session.submitResult = CompletableFuture.completedFuture(
                    Receipt("choose", ReceiptStatus.REJECTED, reason = "ACTION_NOT_AVAILABLE")
                )
            }
            click(s, 500, 160)
            card(s, 1)
            if (stale) {
                val updated = session.snapshot.view.copy(legalActions = listOf(LegalActionView.Pass(2)))
                session.update(updated)
            }
            if (stale) {
                // The authoritative update removed CHI, so the modal has closed and its old
                // confirm hit area must not submit the stale option.
                assertFalse(s.touchDown(720, 160, 0, 0))
                assertTrue(session.submittedChoices.isEmpty())
                click(s, 900, 160)
                assertEquals(listOf(LegalActionView.Pass(2)), session.submitted)
            } else {
                confirm(s)
                assertEquals(1, session.submittedChoices.size,
                    "A rejected receipt still records the attempted CHI option")
                assertEquals(
                    session.snapshot.decision.actions.filterIsInstance<LegalActionView.DeclareChi>()[1],
                    session.submitted.single(),
                    "Rejection must not silently switch to another CHI combination"
                )
            }
            session.flushUi()
            assertFalse(s.meldSelectionModel.isSelecting)
            s.dispose()
        }
    }
}
