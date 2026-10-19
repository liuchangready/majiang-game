package com.majiang.presentation.game

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.Input
import com.badlogic.gdx.math.Rectangle
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.testing.FakeGameSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.presentation.testing.TestUiAssets
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture

/** Actual screen pointer path, with only the external GameSession replaced by a test fixture. */
class TileMarkScreenContractTest : GdxHeadlessTestRunner() {
    private fun snapshot(): GameSnapshotView {
        val tiles = (0..13).map { TestViewBuilder.tile(it) }
        val actions = tiles.map { LegalActionView.DiscardTile(0, it) }
        val decision = DecisionView("round", "decision", 0, actions,
            actions.mapIndexed { index, action -> ActionOptionView("discard-$index", action) })
        return GameSnapshotView("session", "round", "epoch", 1, 1, 1,
            TestViewBuilder.playerView(viewerSeat = 0, myClosedTiles = tiles.take(13), myDrawnTile = tiles.last(), legalActions = actions),
            decision, clocks = listOf(ClockInfo(0, 1000, 24000, 20000, 1000)), cheatPoints = List(4) { 100 },
            controls = listOf(ControlsInfo(0, true, "HUMAN", 0)),
            tileMark = TileMarkView(true, null, 10, 0, 12, (0..13).toList(), "decision", 0, 1, emptyList()))
    }

    private fun click(screen: GameScreen, rect: Rectangle) {
        val x = rect.x + rect.width / 2
        val y = rect.y + rect.height / 2
        if (Gdx.graphics == null || Gdx.graphics.width == 0) screen.touchDown(x.toInt(), y.toInt(), 0, 0)
        else screen.touchDown((x * Gdx.graphics.width / 1280f).toInt(), ((720f - y) * Gdx.graphics.height / 720f).toInt(), 0, 0)
    }

    @Test
    fun `real button and hand clicks submit one mark with no accidental discard and no optimistic effect`() {
        val initial = snapshot()
        val fake = FakeGameSession(initialSnapshot = initial)
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, fake, FakeTableNavigation())
        try {
            screen.startGame()
            val panel = screen.tileMarkPanel
            click(screen, panel.btnOpenRect)
            assertTrue(panel.model.isSelecting)
            for (id in listOf(0, 1, 2, 13)) click(screen, screen.refreshHandTileRects().single { it.tile.tileId == id }.rect)
            assertEquals(setOf(0, 1, 2, 13), panel.model.selectedTileIds.toSet())
            assertTrue(fake.submittedChoices.isEmpty())
            val delayed = CompletableFuture<Receipt>()
            fake.submitResult = delayed
            click(screen, panel.btnConfirmRect)
            click(screen, panel.btnConfirmRect)
            assertEquals(1, fake.submittedMarks.size)
            assertEquals(setOf(0, 1, 2, 13), fake.submittedMarks.single().tileIds.toSet())
            assertTrue(fake.submittedChoices.isEmpty())
            assertEquals(100, screen.currentSnapshot!!.cheatPoints[0])
            assertTrue(screen.currentSnapshot!!.tileMark!!.activeGroups.isEmpty())
            delayed.complete(Receipt(fake.submittedMarks.single().requestId, ReceiptStatus.REJECTED, reason = "DECISION_MISMATCH"))
            fake.flushUi()
            assertTrue(fake.submittedChoices.isEmpty())
            assertEquals(100, screen.currentSnapshot!!.cheatPoints[0])
            click(screen, panel.btnOpenRect)
            assertTrue(panel.model.isSelecting)
            screen.keyDown(Input.Keys.ESCAPE)
            assertFalse(panel.model.isSelecting)
            assertFalse(screen.isDisposed)
        } finally { screen.dispose(); assets.dispose() }
    }

    @Test
    fun `new decision pause and disabled snapshot cancel modal without submitting stale tiles`() {
        val initial = snapshot()
        val fake = FakeGameSession(initialSnapshot = initial)
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, fake, FakeTableNavigation())
        try {
            screen.startGame()
            click(screen, screen.tileMarkPanel.btnOpenRect)
            (0..3).forEach { id -> click(screen, screen.refreshHandTileRects().single { it.tile.tileId == id }.rect) }
            val changed = initial.copy(cursor = 2, decision = initial.decision.copy(decisionId = "next"),
                tileMark = initial.tileMark!!.copy(decisionId = "next"))
            fake.publish(changed)
            fake.flushUi()
            assertFalse(screen.tileMarkPanel.model.isSelecting)
            assertTrue(screen.tileMarkPanel.model.selectedTileIds.isEmpty())
            assertTrue(fake.submittedMarks.isEmpty())
            assertTrue(fake.submittedChoices.isEmpty())
            click(screen, screen.tileMarkPanel.btnOpenRect)
            fake.publish(changed.copy(cursor = 3, paused = true))
            fake.flushUi()
            assertFalse(screen.tileMarkPanel.model.canConfirm)
            fake.publish(changed.copy(cursor = 4, tileMark = null))
            fake.flushUi()
            click(screen, screen.tileMarkPanel.btnOpenRect)
            assertFalse(screen.tileMarkPanel.model.isSelecting)
            assertTrue(fake.submittedMarks.isEmpty())
        } finally { screen.dispose(); assets.dispose() }
    }

    @Test
    fun `reconnecting without a new snapshot blocks confirmation and invalidates selection`() {
        val fake = FakeGameSession(initialSnapshot = snapshot())
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, fake, FakeTableNavigation())
        try {
            screen.startGame()
            val panel = screen.tileMarkPanel
            click(screen, panel.btnOpenRect)
            (0..3).forEach { id -> click(screen, screen.refreshHandTileRects().single { it.tile.tileId == id }.rect) }
            assertTrue(panel.model.canConfirm)
            fake.connectionState = ConnectionState.RECONNECTING
            click(screen, panel.btnConfirmRect)
            assertTrue(fake.submittedMarks.isEmpty())
            assertTrue(fake.submittedChoices.isEmpty())
            assertFalse(panel.model.isSelecting)
            assertTrue(panel.model.selectedTileIds.isEmpty())
            fake.connectionState = ConnectionState.CONNECTED
            assertTrue(fake.submittedMarks.isEmpty())
            click(screen, panel.btnOpenRect)
            assertTrue(panel.model.isSelecting)
            assertFalse(panel.model.canConfirm)
        } finally { screen.dispose(); assets.dispose() }
    }

    @Test
    fun `river hint button toggles and escape or android back closes without leaving the game`() {
        val fake = FakeGameSession(initialSnapshot = snapshot())
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, fake, FakeTableNavigation())
        try {
            screen.startGame()
            assertFalse(screen.riverHintPanel.isOpen)
            click(screen, screen.riverHintPanel.btnToggleRect)
            assertTrue(screen.riverHintPanel.isOpen)
            click(screen, screen.riverHintPanel.btnToggleRect)
            assertFalse(screen.riverHintPanel.isOpen)
            for (key in listOf(Input.Keys.ESCAPE, Input.Keys.BACK)) {
                click(screen, screen.riverHintPanel.btnToggleRect)
                assertTrue(screen.riverHintPanel.isOpen)
                assertTrue(screen.keyDown(key))
                assertFalse(screen.riverHintPanel.isOpen)
                assertFalse(screen.isDisposed)
                assertFalse(fake.closed)
            }
            assertTrue(fake.submittedChoices.isEmpty())
            assertTrue(fake.submittedMarks.isEmpty())
        } finally { screen.dispose(); assets.dispose() }
    }

    @Test
    fun `tile marking modal blocks underlying hint button and keeps selection active`() {
        val fake = FakeGameSession(initialSnapshot = snapshot())
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, fake, FakeTableNavigation())
        try {
            screen.startGame()
            click(screen, screen.tileMarkPanel.btnOpenRect)
            assertTrue(screen.tileMarkPanel.model.isSelecting)
            click(screen, screen.riverHintPanel.btnToggleRect)
            assertFalse(screen.riverHintPanel.isOpen)
            assertTrue(screen.tileMarkPanel.model.isSelecting)
            screen.keyDown(Input.Keys.ESCAPE)
            assertFalse(screen.tileMarkPanel.model.isSelecting)
            assertFalse(screen.isDisposed)
            assertTrue(fake.submittedChoices.isEmpty())
            assertTrue(fake.submittedMarks.isEmpty())
        } finally { screen.dispose(); assets.dispose() }
    }

    @Test
    fun `rendering active selection automatically closes hint without cancelling the selection`() {
        val fake = FakeGameSession(initialSnapshot = snapshot())
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, fake, FakeTableNavigation())
        try {
            screen.startGame()
            click(screen, screen.riverHintPanel.btnToggleRect)
            assertTrue(screen.riverHintPanel.isOpen)
            assertTrue(screen.tileMarkPanel.model.begin())
            screen.render(0f)
            assertFalse(screen.riverHintPanel.isOpen)
            assertTrue(screen.tileMarkPanel.model.isSelecting)
            assertTrue(fake.submittedChoices.isEmpty())
            assertTrue(fake.submittedMarks.isEmpty())
        } finally { screen.dispose(); assets.dispose() }
    }

    @Test
    fun `riichi selection blocks hint toggle and escape cancels the selection first`() {
        val fake = FakeGameSession(initialSnapshot = snapshot())
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, fake, FakeTableNavigation())
        try {
            screen.startGame()
            val discard = TestViewBuilder.tile(0)
            screen.riichiSelectionModel.update(listOf(RiichiPreviewOptionView("riichi",
                LegalActionView.DeclareRiichi(0, discard), listOf(TestViewBuilder.tile(4)), FuritenStatusView())))
            assertTrue(screen.riichiSelectionModel.begin())
            click(screen, screen.riverHintPanel.btnToggleRect)
            assertFalse(screen.riverHintPanel.isOpen)
            assertTrue(screen.riichiSelectionModel.isSelecting)
            assertTrue(screen.keyDown(Input.Keys.ESCAPE))
            assertFalse(screen.riichiSelectionModel.isSelecting)
            assertFalse(screen.isDisposed)
            assertTrue(fake.submittedChoices.isEmpty())
        } finally { screen.dispose(); assets.dispose() }
    }

    @Test
    fun `meld selection blocks hint toggle without submitting a choice`() {
        val fake = FakeGameSession(initialSnapshot = snapshot())
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, fake, FakeTableNavigation())
        try {
            screen.startGame()
            val hand = listOf(TestViewBuilder.tile(0), TestViewBuilder.tile(1))
            val called = TestViewBuilder.tile(2)
            val option = MeldPreviewOptionView("pon", LegalActionView.DeclarePon(0, hand),
                MeldTypeView.PON, hand + called, hand, calledTile = called, fromPlayer = 3)
            screen.meldSelectionModel.update(PlayerPresentationPreviewView("round", "decision", 0,
                meldOptions = listOf(option)))
            assertTrue(screen.meldSelectionModel.begin(MeldChoiceKindView.PON))
            click(screen, screen.riverHintPanel.btnToggleRect)
            assertFalse(screen.riverHintPanel.isOpen)
            assertTrue(screen.meldSelectionModel.isSelecting)
            assertTrue(fake.submittedChoices.isEmpty())
            assertTrue(fake.submittedMarks.isEmpty())
        } finally { screen.dispose(); assets.dispose() }
    }

    @Test
    fun `preparation snapshot blocks hint input and closes already opened hint on render`() {
        val initial = snapshot()
        val fake = FakeGameSession(initialSnapshot = initial)
        val assets = TestUiAssets.create()
        val screen = GameScreen(assets, fake, FakeTableNavigation())
        try {
            screen.startGame()
            click(screen, screen.riverHintPanel.btnToggleRect)
            assertTrue(screen.riverHintPanel.isOpen)
            val preparation = ShufflePreparationView("round", ShufflePreparationStatusView.SELECTING,
                0, 10000, 0, false, null, 0, 100)
            screen.onSnapshotAndEvents(initial.copy(cursor = 2,
                view = initial.view.copy(phase = GamePhaseView.STACKING), tileMark = null,
                preparation = preparation), emptyList())
            screen.render(0f)
            assertFalse(screen.riverHintPanel.isOpen)
            click(screen, screen.riverHintPanel.btnToggleRect)
            assertFalse(screen.riverHintPanel.isOpen)
            assertTrue(fake.submittedChoices.isEmpty())
            assertTrue(fake.submittedMarks.isEmpty())
        } finally { screen.dispose(); assets.dispose() }
    }

    @Test
    fun `actual server rejection codes have Chinese explanations`() {
        val reasons = listOf(
            "TILE_MARK_DISABLED", "TILE_MARK_CAPACITY_EXCEEDED", "INSUFFICIENT_FUNDS",
            "DEADLINE_EXPIRED", "DECISION_MISMATCH", "NOT_YOUR_TURN", "SEAT_NOT_HUMAN",
            "ACTION_IN_FLIGHT", "SESSION_CLOSED", "TILE_MARK_RIICHI_LOCKED",
            "TILE_MARK_INVALID_SELECTION", "TILE_MARK_TILE_NOT_OWNED", "TILE_MARK_ALREADY_MARKED",
            "STALE_CONTROL_EPOCH", "WRONG_PHASE", "NO_ACTIVE_DECISION"
        )
        for (reason in reasons) {
            val text = TileMarkPanel.translateServerReason(reason)
            assertNotEquals(reason, text)
            assertTrue(text.any { it.code > 127 }, "Missing Chinese explanation: $reason")
        }
    }
}
