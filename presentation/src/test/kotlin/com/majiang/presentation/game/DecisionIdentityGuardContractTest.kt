package com.majiang.presentation.game

import com.majiang.client.api.DecisionView
import com.majiang.client.api.GamePhaseView
import com.majiang.client.api.GameSnapshotView
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * DecisionIdentityGuard 独立纯契约与边界测试。
 * 无 engine, server, protocol, GL 依赖。
 */
class DecisionIdentityGuardContractTest {

    private fun createSnapshot(
        sessionId: String = "session-1",
        syncEpoch: String = "epoch-1",
        roundId: String = "round-1",
        decisionId: String = "decision-1",
        controlEpoch: Long = 3L,
        cursor: Long = 10L
    ): GameSnapshotView {
        val tile = TestViewBuilder.tile(0)
        return GameSnapshotView(
            sessionId = sessionId,
            roundId = roundId,
            syncEpoch = syncEpoch,
            cursor = cursor,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = TestViewBuilder.playerView(viewerSeat = 0, myClosedTiles = listOf(tile), myDrawnTile = null)
                .copy(phase = GamePhaseView.CHEAT_WINDOW, currentTurnPlayer = 0),
            decision = DecisionView(roundId = roundId, decisionId = decisionId, controlEpoch = controlEpoch),
            clocks = emptyList(),
            cheatPoints = listOf(40, 40, 40, 40),
            controls = emptyList()
        )
    }

    @Test
    fun `initial state has no identity and reports no decision change`() {
        val guard = DecisionIdentityGuard()
        assertFalse(guard.hasIdentity)
        assertNull(guard.lastSessionId)
        assertNull(guard.lastSyncEpoch)
        assertNull(guard.lastRoundId)
        assertNull(guard.lastDecisionId)
        assertNull(guard.lastControlEpoch)
        assertNull(guard.lastCursor)
        assertNull(guard.lastExtraToken)

        val snap = createSnapshot()
        assertFalse(guard.hasDecisionChanged(snap), "首次观察前不判定为已变更")
        assertFalse(guard.isSameSnapshot(snap))
    }

    @Test
    fun `records snapshot identity and recognizes identical snapshot`() {
        val guard = DecisionIdentityGuard()
        val snap = createSnapshot()

        guard.record(snap, extraToken = "offer-A")
        assertTrue(guard.hasIdentity)
        assertEquals("session-1", guard.lastSessionId)
        assertEquals("epoch-1", guard.lastSyncEpoch)
        assertEquals("round-1", guard.lastRoundId)
        assertEquals("decision-1", guard.lastDecisionId)
        assertEquals(3L, guard.lastControlEpoch)
        assertEquals(10L, guard.lastCursor)
        assertEquals("offer-A", guard.lastExtraToken)

        // 相同快照及相同 extraToken
        assertTrue(guard.isSameSnapshot(snap, extraToken = "offer-A"))
        assertFalse(guard.hasDecisionChanged(snap))

        // extraToken 不一致使得 isSameSnapshot 返回 false
        assertFalse(guard.isSameSnapshot(snap, extraToken = "offer-B"))

        // cursor 发生变化（推进）使得 isSameSnapshot 返回 false，但决策本身未变
        val nextCursorSnap = snap.copy(cursor = 11L)
        assertFalse(guard.isSameSnapshot(nextCursorSnap, extraToken = "offer-A"))
        assertFalse(guard.hasDecisionChanged(nextCursorSnap))
    }

    @Test
    fun `detects individual decision dimension changes`() {
        val guard = DecisionIdentityGuard()
        val base = createSnapshot()
        guard.record(base, extraToken = "offer-1")

        // 逐一验证五大核心决策维度
        val variations = listOf(
            base.copy(sessionId = "session-2"),
            base.copy(syncEpoch = "epoch-2"),
            base.copy(roundId = "round-2", decision = base.decision.copy(roundId = "round-2")),
            base.copy(decision = base.decision.copy(decisionId = "decision-2")),
            base.copy(decision = base.decision.copy(controlEpoch = 4L))
        )

        for (variant in variations) {
            assertTrue(guard.hasDecisionChanged(variant), "必须识别 ${variant.sessionId}/${variant.syncEpoch}/${variant.roundId}/${variant.decision.decisionId}/${variant.decision.controlEpoch} 的变更")
            assertFalse(guard.isSameSnapshot(variant, extraToken = "offer-1"))
        }
    }

    @Test
    fun `field overloads match snapshot behavior`() {
        val guard = DecisionIdentityGuard()
        guard.record(
            sessionId = "s1",
            syncEpoch = "e1",
            roundId = "r1",
            decisionId = "d1",
            controlEpoch = 1L,
            cursor = 5L,
            extraToken = "token"
        )
        assertTrue(guard.hasIdentity)
        assertEquals("s1", guard.lastSessionId)
        assertEquals(5L, guard.lastCursor)

        assertFalse(guard.hasDecisionChanged("s1", "e1", "r1", "d1", 1L))
        assertTrue(guard.hasDecisionChanged("s2", "e1", "r1", "d1", 1L))
        assertTrue(guard.hasDecisionChanged("s1", "e2", "r1", "d1", 1L))
        assertTrue(guard.hasDecisionChanged("s1", "e1", "r2", "d1", 1L))
        assertTrue(guard.hasDecisionChanged("s1", "e1", "r1", "d2", 1L))
        assertTrue(guard.hasDecisionChanged("s1", "e1", "r1", "d1", 2L))
    }

    @Test
    fun `reset clears all recorded dimensions`() {
        val guard = DecisionIdentityGuard()
        guard.record(createSnapshot(), extraToken = "offer")
        assertTrue(guard.hasIdentity)

        guard.reset()
        assertFalse(guard.hasIdentity)
        assertNull(guard.lastSessionId)
        assertNull(guard.lastSyncEpoch)
        assertNull(guard.lastRoundId)
        assertNull(guard.lastDecisionId)
        assertNull(guard.lastControlEpoch)
        assertNull(guard.lastCursor)
        assertNull(guard.lastExtraToken)
    }
}
