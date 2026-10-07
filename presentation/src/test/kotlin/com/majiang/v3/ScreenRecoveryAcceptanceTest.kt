package com.majiang.v3

import com.badlogic.gdx.Gdx
import com.majiang.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.game.GameScreen
import com.majiang.testing.FakeGameSession
import com.majiang.testing.FakeTableNavigation
import com.majiang.testing.TestUiAssets
import com.majiang.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

class ScreenRecoveryAcceptanceTest : GdxHeadlessTestRunner() {

    private fun flushUi() {
        val done = CompletableFuture<Unit>()
        Gdx.app.postRunnable { done.complete(Unit) }
        done.get(3, TimeUnit.SECONDS)
    }

    @Test fun `real bankruptcy restores final win breakdown and routes to final ranking`() {
        val scores = listOf(49900, 100, 25000, 25000)
        val view = TestViewBuilder.playerView(
            viewerSeat = 0,
            phase = GamePhaseView.MATCH_END,
            scores = scores
        )
        val sessionProgress = SessionProgressView(
            lifecycle = "FINISHED",
            scores = scores,
            roundWind = 1,
            roundNumber = 1,
            dealerSeat = 0,
            honba = 0,
            finalResult = PublicSessionResultView(scores, listOf(0, 2, 3, 1), "BANKRUPTCY")
        )
        val winEvent = GameEventView.TsumoWin(
            turnNumber = 1,
            player = 0,
            winTile = TestViewBuilder.tile(SuitView.MAN, 5),
            yakuList = listOf("自摸 1翻"),
            han = 1,
            fu = 30,
            score = 1000
        )
        val snap = GameSnapshotView(
            sessionId = "bankruptcy-session",
            roundId = "round-1",
            syncEpoch = "epoch-1",
            cursor = 10L,
            sessionVersion = 10L,
            roundVersion = 10L,
            view = view,
            decision = DecisionView("round-1", "dec-10", 0L),
            session = sessionProgress,
            settlementRoundId = "round-1",
            settlementEvents = listOf(winEvent),
            settlementDetail = SettlementDetailView(
                title = "自摸和牌",
                winner = 0,
                loser = null,
                winTile = TestViewBuilder.tile(SuitView.MAN, 5),
                yakuList = listOf("自摸 1翻"),
                han = 1,
                fu = 30,
                score = 1000
            )
        )
        val session = FakeGameSession(initialSnapshot = snap)
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        try {
            flushUi()
            assertTrue(screen.isMatchFinished)
            assertTrue(screen.isSettlementShown)
            assertNotNull(screen.currentSettlementData)
            val before = session.snapshot.roundVersion
            screen.touchDown(640, 168, 0, 0)
            assertTrue(screen.isFinalResultShown)
            assertEquals("BANKRUPTCY", session.snapshot.session?.finalResult?.reason)
            flushUi()
            assertEquals(before, session.snapshot.roundVersion)
        } finally { screen.dispose() }
    }
}
