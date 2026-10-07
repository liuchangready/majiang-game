package com.majiang.v3

import com.majiang.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.game.GameScreen
import com.majiang.testing.FakeGameSession
import com.majiang.testing.FakeTableNavigation
import com.majiang.testing.TestUiAssets
import com.majiang.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.time.Duration
import java.util.concurrent.CompletableFuture

class AsyncScreenAcceptanceTest : GdxHeadlessTestRunner() {

    @Test fun `screen input returns without waiting for network and pending blocks repeated win clicks`() {
        val delayedReceipt = CompletableFuture<Receipt>()
        val view = TestViewBuilder.playerView(
            viewerSeat = 0,
            currentTurnPlayer = 0,
            phase = GamePhaseView.DISCARD,
            legalActions = listOf(LegalActionView.DeclareTsumo(0))
        )
        val session = FakeGameSession(initialView = view)
        session.submitResult = delayedReceipt

        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        try {
            assertTrue(screen.canHumanTsumo())
            assertTimeout(Duration.ofMillis(120)) { screen.touchDown(900, 160, 0, 0) }
            assertEquals(1, session.submittedChoices.size)
            assertFalse(screen.canHumanTsumo())
            screen.touchDown(900, 160, 0, 0)
            assertEquals(1, session.submittedChoices.size)
            assertFalse(screen.isSettlementShown, "Submission alone must not invent a winning settlement")
            delayedReceipt.complete(Receipt("win", ReceiptStatus.RECORDED))
            assertFalse(screen.isSettlementShown, "Recorded is not a committed win event")
        } finally { screen.dispose() }
    }
}
