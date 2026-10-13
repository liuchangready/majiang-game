package com.majiang.presentation.game

import com.badlogic.gdx.Gdx
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.*
import com.majiang.presentation.testing.FakeGameSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.presentation.testing.TestUiAssets
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.util.Collections
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

class EndTenpaiScreenAcceptanceTest : GdxHeadlessTestRunner() {

    companion object {
        @JvmStatic fun settled(finished: Boolean, details: Boolean): GameSnapshotView {
            val scores = listOf(51000, -1000, 25000, 25000)
            val view = TestViewBuilder.playerView(
                viewerSeat = 0,
                phase = GamePhaseView.ROUND_END,
                scores = scores
            )
            val sessionProgress = SessionProgressView(
                lifecycle = if (finished) "FINISHED" else "WAITING_READY",
                scores = scores,
                roundWind = 1,
                roundNumber = 0,
                dealerSeat = 0,
                honba = 0,
                finalResult = if (finished) PublicSessionResultView(scores, listOf(0, 2, 3, 1), "BANKRUPTCY") else null
            )
            val settlementEvents = if (details) listOf(
                GameEventView.DrawGame(10, DrawReasonView.EXHAUSTIVE, listOf(0)),
                GameEventView.ScoreSettled(10, mapOf(0 to 3000, 1 to -1000, 2 to -1000, 3 to -1000), scores)
            ) else emptyList()
            val settlementDetail = if (details) SettlementDetailView(
                title = "流局",
                winner = null,
                loser = null,
                winTile = null,
                yakuList = listOf("荒牌流局"),
                han = 0,
                fu = 0,
                score = 0,
                isDraw = true,
                drawReason = "荒牌流局",
                payments = mapOf(0 to 3000, 1 to -1000, 2 to -1000, 3 to -1000),
                newScores = scores
            ) else null

            return GameSnapshotView(
                sessionId = "match",
                roundId = "round",
                syncEpoch = "epoch",
                cursor = 4L,
                sessionVersion = 4L,
                roundVersion = 4L,
                view = view,
                decision = DecisionView("round", "finished", 0L),
                session = sessionProgress,
                settlementRoundId = if (details) "round" else null,
                settlementEvents = settlementEvents,
                settlementDetail = settlementDetail
            )
        }

        @JvmStatic fun waiting(orphans: Boolean): GameSnapshotView {
            val tiles = if (orphans) listOf(SuitView.MAN, SuitView.PIN, SuitView.SOU).flatMap { listOf(TestViewBuilder.tile(it, 1), TestViewBuilder.tile(it, 9)) } +
                (1..4).map { TestViewBuilder.tile(SuitView.WIND, it) } + (1..3).map { TestViewBuilder.tile(SuitView.DRAGON, it) }
            else (1..6).map { TestViewBuilder.tile(SuitView.MAN, it) } + (7..9).map { TestViewBuilder.tile(SuitView.PIN, it) } + (2..5).map { TestViewBuilder.tile(SuitView.SOU, it) }
            val view = TestViewBuilder.playerView(
                viewerSeat = 0,
                phase = GamePhaseView.RESPONSE_WINDOW,
                currentTurnPlayer = 1,
                myClosedTiles = tiles,
                myDrawnTile = null
            )
            val waits = if (orphans) (0..12).map { TestViewBuilder.tile(it) } else listOf(TestViewBuilder.tile(SuitView.SOU, 2), TestViewBuilder.tile(SuitView.SOU, 5))
            val preview = PlayerPresentationPreviewView(
                roundId = "round",
                decisionId = "waiting",
                controlEpoch = 0L,
                waits = waits
            )
            return GameSnapshotView(
                sessionId = "match",
                roundId = "round",
                syncEpoch = "epoch",
                cursor = 0L,
                sessionVersion = 0L,
                roundVersion = 0L,
                view = view,
                decision = DecisionView("round", "waiting", 0L),
                presentationPreview = preview
            )
        }
    }

    private fun flushUi() {
        val done = CompletableFuture<Unit>()
        Gdx.app.postRunnable { done.complete(Unit) }
        done.get(3, TimeUnit.SECONDS)
    }

    @Test fun `finished session with round end view never requests another round`() {
        val session = FakeGameSession(initialSnapshot = settled(true, true))
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        try {
            assertTrue(screen.isSettlementShown)
            screen.touchDown(640, 168, 0, 0)
            assertEquals(0, session.nextRoundCalls, "FINISHED must override the round's ROUND_END phase")
            assertEquals(0, session.submitted.size)
            assertFalse(screen.canHumanTsumo())
            assertTrue(screen.isMatchFinished)
            assertTrue(screen.isFinalResultShown)
        } finally { screen.dispose() }
    }

    @Test fun `rejected or failed next round preserves settlement and pending blocks repeated clicks`() {
        for (failure in listOf(false, true)) {
            val session = FakeGameSession(initialSnapshot = settled(false, true))
            val nav = FakeTableNavigation()
            val screen = GameScreen(TestUiAssets.create(), session, nav)
            screen.startGame()
            try {
                screen.touchDown(640, 168, 0, 0)
                screen.touchDown(640, 168, 0, 0)
                assertEquals(1, session.nextRoundCalls)
                assertTrue(screen.isSettlementShown)
                flushUi()
                assertTrue(screen.isSettlementShown)
                assertNotNull(screen.currentSettlementData)
            } finally { screen.dispose() }
        }
    }

    @Test fun `native match end click shows ranking and repeated snapshots keep it open`() {
        val snap = settled(true, true)
        val snapMatchEnd = snap.copy(view = snap.view.copy(phase = GamePhaseView.MATCH_END))
        val session = FakeGameSession(initialSnapshot = snapMatchEnd)
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        try {
            screen.touchDown(640, 168, 0, 0)
            assertTrue(screen.isFinalResultShown, "Closing details alone strands the player at a dead table")
            session.publish(session.snapshot.copy(cursor = 5))
            flushUi()
            assertTrue(screen.isFinalResultShown)
            assertEquals(0, session.nextRoundCalls)
        } finally { screen.dispose() }
    }

    @Test fun `finished snapshot without settlement events restores final result immediately`() {
        val session = FakeGameSession(initialSnapshot = settled(true, false))
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        try {
            assertTrue(screen.isMatchFinished)
            assertTrue(screen.isFinalResultShown)
            session.publish(session.snapshot.copy(syncEpoch = "reconnected", cursor = 5))
            flushUi()
            assertTrue(screen.isFinalResultShown)
            assertEquals(0, session.nextRoundCalls)
        } finally { screen.dispose() }
    }

    @Test fun `final modal swallows underlying takeover and gameplay controls`() {
        val session = FakeGameSession(initialSnapshot = settled(true, false))
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        try {
            screen.touchDown(1150, 126, 0, 0)
            screen.touchDown(900, 160, 0, 0)
            assertEquals(0, session.claimControlCalls)
            assertEquals(0, session.submitted.size)
            assertEquals(0, session.nextRoundCalls)
            assertTrue(screen.isFinalResultShown)
        } finally { screen.dispose() }
    }

    @Test fun `disconnected final result still allows navigating local round details`() {
        val session = FakeGameSession(initialSnapshot = settled(true, true))
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        try {
            screen.touchDown(640, 168, 0, 0)
            session.connectionState = ConnectionState.CLOSED
            screen.touchDown(370, 168, 0, 0)
            assertFalse(screen.isFinalResultShown)
            assertTrue(screen.isSettlementShown)
            screen.touchDown(640, 168, 0, 0)
            assertTrue(screen.isFinalResultShown)
            assertEquals(0, session.nextRoundCalls)
        } finally { screen.dispose() }
    }

    @Test fun `waits refresh from snapshots and clear at settlement`() {
        val session = FakeGameSession(initialSnapshot = waiting(false))
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        try {
            assertEquals(2, screen.tenpaiWaits.size)
            session.publish(waiting(true).copy(cursor = 1))
            flushUi()
            assertEquals(13, screen.tenpaiWaits.size)
            session.publish(settled(true, true))
            flushUi()
            assertTrue(screen.tenpaiWaits.isEmpty())
        } finally { screen.dispose() }
    }

    @Test fun `successful next round waits for authoritative new round snapshot to clear details`() {
        val session = FakeGameSession(initialSnapshot = settled(false, true))
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        try {
            screen.touchDown(640, 168, 0, 0)
            flushUi()
            assertTrue(screen.isSettlementShown)
            session.publish(waiting(false).copy(
                roundId = "next-round",
                cursor = 5,
                decision = DecisionView("next-round", "waiting", 0L),
                presentationPreview = waiting(false).presentationPreview?.copy(roundId = "next-round")
            ))
            flushUi()
            assertFalse(screen.isSettlementShown)
            assertFalse(screen.isFinalResultShown)
        } finally { screen.dispose() }
    }
}
